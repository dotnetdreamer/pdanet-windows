import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URI;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Minimal HTTP/HTTPS forward proxy, pure java.* so it dexes and runs on Android via
 * `app_process` (as the shell user) or later inside a real app (own uid). Because it runs
 * in a normal process, the phone's VPN captures its outbound sockets — which is what gives
 * tethered PC traffic the phone's VPN exit.
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
