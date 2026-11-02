import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URI;
import java.util.Arrays;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Minimal HTTP/HTTPS forward proxy, pure java.* so it dexes and runs on Android via
 * `app_process` (as the shell user) or later inside a real app (own uid). Because it runs
 * in a normal process, the phone's VPN captures its outbound sockets — which is what gives
 * tethered PC traffic the phone's VPN exit.
 *
 * It also relays UDP for screen sharing through the phone: a "PDANET-UDP" request turns the
 * connection into one UDP socket on the phone, see handleUdp.
 *
 * PC side: adb forward tcp:<pcPort> tcp:<thisPort>, then point Windows at 127.0.0.1:<pcPort>.
 *
 * Build:  javac ProxyMain.java  ->  d8 ProxyMain*.class --min-api 21 --output out
 * Run:    CLASSPATH=/data/local/tmp/proxy.dex app_process /system/bin ProxyMain 8899
 */
public class ProxyMain {

    public static void main(String[] args) throws Exception {
        int port = args.length > 0 ? Integer.parseInt(args[0]) : 8899;
        ServerSocket server = new ServerSocket();
        server.setReuseAddress(true);
        server.bind(new InetSocketAddress("127.0.0.1", port));
        System.out.println("pdanet-agent listening on 127.0.0.1:" + port);

        final ExecutorService pool = Executors.newCachedThreadPool();
        while (true) {
            final Socket client = server.accept();
            pool.submit(new Runnable() {
                public void run() { handle(client); }
            });
        }
    }

    static void handle(Socket client) {
        Socket up = null;
        try {
            client.setTcpNoDelay(true);
            InputStream cin = client.getInputStream();
            OutputStream cout = client.getOutputStream();

            String head = readHead(cin);
            if (head == null) { safeClose(client); return; }

            int eol = head.indexOf("\r\n");
            String firstLine = eol >= 0 ? head.substring(0, eol) : head;
            String[] parts = firstLine.split(" ");
            if (parts.length < 2) { safeClose(client); return; }

            String method = parts[0];
            String target = parts[1];
            String version = parts.length > 2 ? parts[2] : "HTTP/1.1";

            if (method.equals("PDANET-UDP")) {
                handleUdp(client, cin, cout);
                return;
            }
            if (method.equalsIgnoreCase("CONNECT")) {
                int idx = target.lastIndexOf(':');
                String host = idx > 0 ? target.substring(0, idx) : target;
                int port = idx > 0 ? Integer.parseInt(target.substring(idx + 1)) : 443;
                up = new Socket();
                up.setTcpNoDelay(true);
                up.connect(new InetSocketAddress(host, port), 15000);
                cout.write("HTTP/1.1 200 Connection established\r\n\r\n".getBytes("ASCII"));
                cout.flush();
                pipe(client, up);
            } else {
                URI uri = new URI(target);
                String host = uri.getHost();
                int port = uri.getPort() == -1 ? 80 : uri.getPort();
                if (host == null) { safeClose(client); return; }
                String pq = (uri.getRawPath() == null || uri.getRawPath().isEmpty()) ? "/" : uri.getRawPath();
                if (uri.getRawQuery() != null) pq += "?" + uri.getRawQuery();

                up = new Socket();
                up.setTcpNoDelay(true);
                up.connect(new InetSocketAddress(host, port), 15000);
                OutputStream uout = up.getOutputStream();
                String rewritten = method + " " + pq + " " + version + "\r\n";
                String rest = eol >= 0 ? head.substring(eol + 2) : "\r\n";
                uout.write(rewritten.getBytes("ASCII"));
                uout.write(rest.getBytes("ASCII"));
                uout.flush();
                pipe(client, up);
            }
        } catch (Exception e) {
            safeClose(client);
            safeClose(up);
        }
    }

    /**
     * One UDP socket on the phone for PdaNet screen sharing, so the screen goes out through the
     * phone's network and its VPN like everything else. The reply carries the phone's own
     * address in X-Local. After it, each datagram is a frame both ways:
     *
     *   u16 length of what follows | u8 family | address | u16 port | payload
     *
     * Family 4 has a 4 byte IPv4 address, 6 a 16 byte IPv6 address. Family 0 looks up a name
     * on the phone: "R <id> <name>" comes in, "A <id> <ip>..." or "E <id> <why>" goes out.
     * Closing the connection closes the socket.
     */
    static void handleUdp(final Socket client, InputStream cin, final OutputStream cout) {
        final DatagramSocket udp;
        try {
            udp = new DatagramSocket();
            String head = "HTTP/1.1 200 OK\r\nX-Local: " + localAddress() + ":" + udp.getLocalPort() + "\r\n\r\n";
            cout.write(head.getBytes("ASCII"));
            cout.flush();
        } catch (Exception e) {
            safeClose(client);
            return;
        }

        Thread back = new Thread(new Runnable() {
            public void run() {
                byte[] buf = new byte[65535];
                DatagramPacket p = new DatagramPacket(buf, buf.length);
                try {
                    while (true) {
                        p.setLength(buf.length);
                        udp.receive(p);
                        writeFrame(cout, p.getAddress(), p.getPort(), buf, p.getLength());
                    }
                } catch (Exception ignored) {}
                safeClose(client);
            }
        });
        back.setDaemon(true);
        back.start();

        try {
            DataInputStream in = new DataInputStream(new BufferedInputStream(cin, 65536));
            byte[] frame = new byte[65535];
            while (true) {
                int len = in.readUnsignedShort();
                if (len < 1) break;
                in.readFully(frame, 0, len);
                int family = frame[0] & 0xff;
                if (family == 0) {
                    lookup(cout, new String(frame, 1, len - 1, "UTF-8"));
                    continue;
                }
                int alen = family == 4 ? 4 : family == 6 ? 16 : -1;
                if (alen < 0 || len < 3 + alen) continue;
                InetAddress to = InetAddress.getByAddress(Arrays.copyOfRange(frame, 1, 1 + alen));
                int port = ((frame[1 + alen] & 0xff) << 8) | (frame[2 + alen] & 0xff);
                try {
                    udp.send(new DatagramPacket(frame, 3 + alen, len - 3 - alen, to, port));
                } catch (IOException ignored) {
                    // An address the phone cannot reach right now, like a dropped datagram.
                }
            }
        } catch (Exception ignored) {}
        udp.close();
        safeClose(client);
    }

    static void writeFrame(OutputStream out, InetAddress from, int port, byte[] data, int n) throws IOException {
        byte[] ip = from.getAddress();
        int len = 3 + ip.length + n;
        byte[] f = new byte[2 + len];
        f[0] = (byte) (len >> 8);
        f[1] = (byte) len;
        f[2] = (byte) (ip.length == 4 ? 4 : 6);
        System.arraycopy(ip, 0, f, 3, ip.length);
        f[3 + ip.length] = (byte) (port >> 8);
        f[4 + ip.length] = (byte) port;
        System.arraycopy(data, 0, f, 5 + ip.length, n);
        synchronized (out) {
            out.write(f);
            out.flush();
        }
    }

    static void lookup(OutputStream out, String query) throws IOException {
        String[] q = query.trim().split(" ");
        String id = q.length > 1 ? q[1] : "0";
        StringBuilder sb = new StringBuilder();
        try {
            InetAddress[] all = InetAddress.getAllByName(q.length > 2 ? q[2] : "");
            sb.append("A ").append(id);
            for (InetAddress a : all) sb.append(' ').append(a.getHostAddress());
        } catch (Exception e) {
            sb.setLength(0);
            sb.append("E ").append(id).append(" not found");
        }
        byte[] b = sb.toString().getBytes("UTF-8");
        byte[] f = new byte[3 + b.length];
        f[0] = (byte) ((1 + b.length) >> 8);
        f[1] = (byte) (1 + b.length);
        System.arraycopy(b, 0, f, 3, b.length);
        synchronized (out) {
            out.write(f);
            out.flush();
        }
    }

    /** The phone's address on the way out: a connected UDP socket picks the route, nothing is sent. */
    static String localAddress() {
        try {
            DatagramSocket probe = new DatagramSocket();
            probe.connect(InetAddress.getByName("8.8.8.8"), 53);
            String a = probe.getLocalAddress().getHostAddress();
            probe.close();
            return a;
        } catch (Exception e) {
            return "0.0.0.0";
        }
    }

    static String readHead(InputStream in) throws Exception {
        ByteArrayOutputStream buf = new ByteArrayOutputStream();
        int b;
        while ((b = in.read()) != -1) {
            buf.write(b);
            int n = buf.size();
            if (b == '\n' && n >= 4) {
                byte[] a = buf.toByteArray();
                if (a[n - 1] == '\n' && a[n - 2] == '\r' && a[n - 3] == '\n' && a[n - 4] == '\r') break;
            }
            if (n > 65536) break;
        }
        if (buf.size() == 0) return null;
        return new String(buf.toByteArray(), "ASCII");
    }

    static void pipe(final Socket a, final Socket b) {
        Thread t1 = new Thread(new Runnable() { public void run() { copy(a, b); } });
        Thread t2 = new Thread(new Runnable() { public void run() { copy(b, a); } });
        t1.start();
        t2.start();
        try { t1.join(); } catch (InterruptedException ignored) {}
        safeClose(a);
        safeClose(b);
    }

    static void copy(Socket from, Socket to) {
        try {
            InputStream in = from.getInputStream();
            OutputStream out = to.getOutputStream();
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) > 0) { out.write(buf, 0, n); out.flush(); }
        } catch (Exception ignored) {}
        try { to.shutdownOutput(); } catch (Exception ignored) {}
    }

    static void safeClose(Socket s) {
        if (s != null) { try { s.close(); } catch (Exception ignored) {} }
    }
}
