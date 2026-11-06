// PdaNet browser MCP: the in-page helper.
//
// The C# BrowserSession runs this in an isolated JavaScript world of the page's main frame. The
// world shares the page's DOM but not its globals, so the page can neither see nor tamper with
// what the agent reads or with the handles it acts on.
//
// It gives the agent its "eyes": an accessibility style outline of the page in which every element
// the agent can act on carries a [ref=eN] handle. A handle names one element for as long as that
// element lives: the same element keeps its handle across snapshots, a new element gets a new
// number, and numbers are unique across tabs because the session hands each snapshot the next free
// number. A handle whose element left the page is reported as gone, never matched to another one.
//
// The outline descends into open shadow roots (in render order, through slots) and same-origin
// iframes, and coordinates add the frame offsets so a click on an element in a frame lands on it.
// Nothing here ever returns the value of a password field.
(function () {
  "use strict";
  if (globalThis.__pda && globalThis.__pda.v === 3) return;

  var ids = new WeakMap();   // element -> number
  var byId = new Map();      // number -> WeakRef(element)
  var next = 1;

  // ============================ Small helpers ============================
  function norm(s) { return (s == null ? "" : String(s)).replace(/[\s\u00a0\u200b\u2028\u2029]+/g, " ").trim(); }
  function clip(s, n) { s = norm(s); return s.length > n ? s.slice(0, n - 1) + "…" : s; }
  function q(s) { return '"' + String(s).replace(/\\/g, "\\\\").replace(/"/g, '\\"') + '"'; }
  function tagOf(el) { return (el.localName || el.tagName || "").toLowerCase(); }
  function win(el) { var d = el.ownerDocument; return (d && d.defaultView) || window; }
  function css(el) { try { return win(el).getComputedStyle(el); } catch (e) { return null; } }
  function indent(d) { var s = ""; for (var i = 0; i < d; i++) s += "  "; return s; }
  function attr(el, n) { return el.getAttribute ? el.getAttribute(n) : null; }

  function frameDoc(el) {
    try { var d = el.contentDocument; return d && d.documentElement ? d : null; } catch (e) { return null; }
  }

  // Children in render order: an open shadow root replaces the light children, a slot shows what
  // is assigned to it (or its fallback content).
  function kids(node) {
    if (node.nodeType === 1) {
      if (node.shadowRoot) return node.shadowRoot.childNodes;
      if (tagOf(node) === "slot" && node.assignedNodes) {
        var a = node.assignedNodes({ flatten: true });
        return a.length ? a : node.childNodes;
      }
    }
    return node.childNodes;
  }

  // Text of an element for its name, skipping scripts, styles and parts hidden from assistive tech.
  function textOf(el, max) {
    var out = "", stack = [el];
    while (stack.length && out.length <= max) {
      var n = stack.pop();
      if (n.nodeType === 3) { out += " " + n.nodeValue; continue; }
      if (n.nodeType !== 1 && n.nodeType !== 11) continue;
      if (n.nodeType === 1) {
        var t = tagOf(n);
        if (t === "script" || t === "style" || t === "noscript" || t === "template") continue;
        if (n !== el && attr(n, "aria-hidden") === "true") continue;
        if (t === "img" && attr(n, "alt")) { out += " " + attr(n, "alt"); continue; }
      }
      var c = kids(n);
      for (var i = c.length - 1; i >= 0; i--) stack.push(c[i]);
    }
    return clip(out, max);
  }

  function byIdIn(el, id) {
    var root = el.getRootNode ? el.getRootNode() : null;
    var hit = root && root.getElementById ? root.getElementById(id) : null;
    return hit || (el.ownerDocument ? el.ownerDocument.getElementById(id) : null);
  }

  // The element that has the keyboard, through shadow roots and same-origin frames.
  function deepActive() {
    var a = document.activeElement, guard = 0;
    while (a && guard++ < 20) {
      if (a.shadowRoot && a.shadowRoot.activeElement) { a = a.shadowRoot.activeElement; continue; }
      var t = tagOf(a);
      if (t === "iframe" || t === "frame") { var d = frameDoc(a); if (d && d.activeElement && d.activeElement !== d.body) { a = d.activeElement; continue; } }
      break;
    }
    return a;
  }

  // ============================ Roles and names ============================
  var INPUT_ROLE = {
    checkbox: "checkbox", radio: "radio", range: "slider", number: "spinbutton", search: "searchbox",
    button: "button", submit: "button", reset: "button", image: "button", file: "button", color: "button"
  };
  var TAG_ROLE = {
    button: "button", textarea: "textbox", option: "option", nav: "navigation", main: "main",
    aside: "complementary", form: "form", search: "search", dialog: "dialog", ul: "list", ol: "list",
    menu: "list", table: "table", tr: "row", td: "cell", th: "columnheader", article: "article",
    summary: "button", details: "group", fieldset: "group", progress: "progressbar", meter: "meter",
    iframe: "iframe", frame: "iframe", video: "video", audio: "audio", canvas: "canvas", hr: "separator"
  };

  function roleOf(el) {
    var r = attr(el, "role");
    if (r) {
      r = r.trim().split(/\s+/)[0].toLowerCase();
      return r === "none" || r === "presentation" ? "" : r;
    }
    var t = tagOf(el);
    if (/^h[1-6]$/.test(t)) return "heading";
    switch (t) {
      case "a": case "area": return el.hasAttribute("href") ? "link" : "";
      case "input":
        var ty = (attr(el, "type") || "text").toLowerCase();
        return ty === "hidden" ? "" : INPUT_ROLE[ty] || "textbox";
      case "select": return el.multiple || el.size > 1 ? "listbox" : "combobox";
      case "img": return attr(el, "alt") === "" ? "" : "img";
      case "header": return el.closest && el.closest("article,aside,main,nav,section") ? "" : "banner";
      case "footer": return el.closest && el.closest("article,aside,main,nav,section") ? "" : "contentinfo";
      case "section": return attr(el, "aria-label") || attr(el, "aria-labelledby") ? "region" : "";
    }
    if (TAG_ROLE[t]) return TAG_ROLE[t];
    if (el.isContentEditable && !(el.parentElement && el.parentElement.isContentEditable)) return "textbox";
    return "";
  }

  var NAME_FROM_CONTENT = {
    link: 1, button: 1, heading: 1, tab: 1, menuitem: 1, menuitemcheckbox: 1, menuitemradio: 1, option: 1,
    treeitem: 1, cell: 1, columnheader: 1, rowheader: 1, switch: 1, checkbox: 1, radio: 1, clickable: 1,
    tooltip: 1, gridcell: 1, row: 1
  };

  function nameOf(el, role) {
    var lb = attr(el, "aria-labelledby");
    if (lb) {
      var s = lb.split(/\s+/).map(function (id) { var e = byIdIn(el, id); return e ? textOf(e, 120) : ""; }).join(" ");
      if (norm(s)) return clip(s, 120);
    }
    var al = attr(el, "aria-label");
    if (norm(al)) return clip(al, 120);
    var t = tagOf(el);
    if (t === "input" || t === "textarea" || t === "select") {
      var ty = (el.type || "").toLowerCase();
      if (ty === "submit" || ty === "button" || ty === "reset")
        return clip(el.value || (ty === "submit" ? "Submit" : ty === "reset" ? "Reset" : ""), 120);
      if (ty === "image") return clip(attr(el, "alt") || el.value || "Submit", 120);
      var lab = el.labels && el.labels.length ? textOf(el.labels[0], 120) : "";
      if (lab) return lab;
      // Never fall back to the value: it can hold a password or another secret.
      return clip(attr(el, "placeholder") || attr(el, "title") || attr(el, "aria-placeholder") || attr(el, "name") || "", 120);
    }
    if (t === "img" || t === "area") return clip(attr(el, "alt") || attr(el, "title") || "", 120);
    if (t === "iframe" || t === "frame") return clip(attr(el, "title") || attr(el, "name") || "", 120);
    if (t === "svg") { var st = el.querySelector("title"); return clip(st ? st.textContent : attr(el, "title") || "", 120); }
    if (t === "fieldset") { var lg = el.querySelector("legend"); if (lg) return textOf(lg, 120); }
    if (t === "table") { var cap = el.querySelector("caption"); if (cap) return textOf(cap, 120); }
    if (NAME_FROM_CONTENT[role] || t === "a" || t === "button" || t === "summary") {
      var c = textOf(el, 160);
      if (c) return c;
      var icon = el.querySelector("[aria-label],img[alt]:not([alt='']),svg title");
      if (icon) return clip(attr(icon, "aria-label") || attr(icon, "alt") || icon.textContent || "", 120);
    }
    return clip(attr(el, "title") || "", 120);
  }

  var INTERACTIVE = {
    link: 1, button: 1, textbox: 1, searchbox: 1, combobox: 1, listbox: 1, option: 1, checkbox: 1, radio: 1,
    "switch": 1, slider: 1, spinbutton: 1, tab: 1, menuitem: 1, menuitemcheckbox: 1, menuitemradio: 1,
    treeitem: 1, scrollbar: 1
  };
  var STRUCTURAL = {
    navigation: 1, main: 1, banner: 1, contentinfo: 1, complementary: 1, form: 1, search: 1, region: 1,
    dialog: 1, alertdialog: 1, alert: 1, list: 1, table: 1, article: 1, heading: 1, tablist: 1, menu: 1,
    menubar: 1, toolbar: 1, tree: 1, grid: 1, group: 1, status: 1, img: 1, progressbar: 1, meter: 1, video: 1,
    audio: 1
  };

  function actionable(el, role, st, inPointer) {
    if (INTERACTIVE[role]) return true;
    var t = tagOf(el);
    if (t === "video" || t === "audio") return el.controls;
    if (t === "label") return false;
    if (el.hasAttribute("onclick") || attr(el, "draggable") === "true") return true;
    var ti = attr(el, "tabindex");
    if (ti != null && +ti >= 0 && (role || (st && st.cursor === "pointer"))) return true;
    // Modern apps make <div>s clickable with a script and a pointer cursor; the outermost one counts.
    if (st && st.cursor === "pointer" && !inPointer) return true;
    return false;
  }

  // ============================ Handles ============================
  function refOf(el) {
    var id = ids.get(el);
    if (!id) { id = next++; ids.set(el, id); }
    var w = byId.get(id);
    if (!w || w.deref() !== el) byId.set(id, new WeakRef(el));
    return "e" + id;
  }

  function lookup(ref) {
    var m = /^e(\d+)$/.exec(String(ref == null ? "" : ref).trim());
    if (!m) return { missing: true, bad: true };
    var w = byId.get(+m[1]), el = w && w.deref();
    if (!el) return { missing: true };
    if (!el.isConnected || !shown(el)) return { gone: true };
    return { el: el };
  }

  // Whether the element's document is still the one its frame shows. A frame that went to another
  // page leaves its old document behind with every element in it still "connected" to it.
  function shown(el) {
    var d = el.ownerDocument;
    for (var guard = 0; d && guard < 20; guard++) {
      var w = d.defaultView;
      if (!w) return false;
      if (w === window) return d === document;
      var fe;
      try { fe = w.frameElement; } catch (e) { return true; } // a parent from another site: cannot tell
      if (!fe || !fe.isConnected || fe.contentDocument !== d) return false;
      d = fe.ownerDocument;
    }
    return !!d;
  }

  function fail(f) { return { ok: false, missing: !!f.missing, gone: !!f.gone, bad: !!f.bad }; }

  function prune() {
    byId.forEach(function (w, id) { var el = w.deref(); if (!el || !el.isConnected || !shown(el)) byId.delete(id); });
  }

  // ============================ Lines ============================
  function urlOf(el) {
    var h = el.href;
    if (!h || typeof h !== "string" || /^javascript:/i.test(h)) return "";
    try {
      var loc = win(el).location, u = new URL(h, loc.href);
      if (u.origin === loc.origin && u.pathname === loc.pathname && u.search === loc.search) h = u.hash || u.pathname;
      else if (u.origin === loc.origin && u.protocol !== "file:") h = u.pathname + u.search + u.hash;
    } catch (e) { /* keep as is */ }
    return "(" + clip(h, 100) + ")";
  }

  function valueOf(el, role) {
    var t = tagOf(el);
    if (t === "select") {
      var sel = [];
      for (var i = 0; i < el.options.length; i++) if (el.options[i].selected) sel.push(norm(el.options[i].label || el.options[i].text));
      return sel.length ? "value=" + q(clip(sel.join(", "), 80)) : "";
    }
    if (t === "input" || t === "textarea") {
      var ty = (el.type || "").toLowerCase();
      if (ty === "password") return el.value ? "[filled]" : "";
      if (ty === "file") {
        var names = [];
        for (var j = 0; el.files && j < el.files.length; j++) names.push(el.files[j].name);
        return names.length ? "files=" + q(clip(names.join(", "), 100)) : "";
      }
      if (ty === "range") return "value=" + q(el.value) + " min=" + (el.min || 0) + " max=" + (el.max || 100);
      if (/^(checkbox|radio|submit|button|reset|image|hidden|color)$/.test(ty)) return "";
      return el.value ? "value=" + q(clip(el.value, 100)) : "";
    }
    if (el.isContentEditable && role === "textbox") { var v = textOf(el, 100); return v ? "value=" + q(v) : ""; }
    var now = attr(el, "aria-valuetext") || attr(el, "aria-valuenow");
    if (now && (role === "slider" || role === "spinbutton" || role === "progressbar" || role === "meter" || role === "scrollbar"))
      return "value=" + q(clip(now, 40));
    if (t === "progress" || t === "meter") return "value=" + q(String(el.value));
    return "";
  }

  function optionsOf(el) {
    var o = el.options;
    if (!o || !o.length) return "";
    var names = [], n = Math.min(o.length, 12);
    for (var i = 0; i < n; i++) names.push(clip(o[i].label || o[i].text, 32).replace(/\|/g, "/"));
    return "options=[" + names.join("|") + (o.length > n ? "|… +" + (o.length - n) + " more" : "") + "]";
  }

  function checkedOf(el, role) {
    var t = tagOf(el);
    if (t === "input" && (el.type === "checkbox" || el.type === "radio")) return el.indeterminate ? "mixed" : el.checked ? "on" : "off";
    var ac = attr(el, "aria-checked");
    if (ac === "mixed") return "mixed";
    if (ac === "true") return "on";
    if (ac === "false" || role === "checkbox" || role === "radio" || role === "switch") return "off";
    return "";
  }

  function flagsOf(el, role, focused) {
    var f = [];
    if (role === "heading") {
      var lv = attr(el, "aria-level") || (tagOf(el).match(/^h([1-6])$/) || [])[1];
      if (lv) f.push("[level=" + lv + "]");
    }
    if (el.disabled || attr(el, "aria-disabled") === "true") f.push("[disabled]");
    if (role === "checkbox" || role === "radio" || role === "switch" || role === "menuitemcheckbox" || role === "menuitemradio") {
      var c = checkedOf(el, role);
      if (c) f.push(c === "on" ? "[checked]" : c === "mixed" ? "[mixed]" : "[unchecked]");
    }
    var pr = attr(el, "aria-pressed");
    if (pr === "true") f.push("[pressed]");
    var ex = attr(el, "aria-expanded");
    if (ex === "true") f.push("[expanded]"); else if (ex === "false") f.push("[collapsed]");
    if (tagOf(el) === "details") f.push(el.open ? "[expanded]" : "[collapsed]");
    if (attr(el, "aria-selected") === "true") f.push("[selected]");
    var cur = attr(el, "aria-current");
    if (cur && cur !== "false") f.push("[current]");
    if (el.required || attr(el, "aria-required") === "true") f.push("[required]");
    if (el.readOnly || attr(el, "aria-readonly") === "true") f.push("[readonly]");
    if (attr(el, "aria-invalid") === "true" || (el.validity && !el.validity.valid && el.value)) f.push("[invalid]");
    if ((role === "dialog" || role === "alertdialog") && (attr(el, "aria-modal") === "true" || (tagOf(el) === "dialog" && el.matches(":modal")))) f.push("[modal]");
    if (el === focused) f.push("[focused]");
    return f;
  }

  // ============================ Snapshot ============================
  // Containers get a handle too, so an agent can outline, read, scroll or capture just that part.
  var CONTAINER_REF = {
    navigation: 1, main: 1, banner: 1, contentinfo: 1, complementary: 1, form: 1, search: 1, region: 1, dialog: 1,
    alertdialog: 1, list: 1, table: 1, article: 1, tablist: 1, menu: 1, menubar: 1, toolbar: 1, tree: 1, grid: 1,
    group: 1, video: 1, audio: 1
  };

  var SKIP = { script: 1, style: 1, noscript: 1, template: 1, head: 1, meta: 1, link: 1, title: 1, base: 1, param: 1, source: 1, track: 1 };

  function hasInteractive(el) {
    try { return !!el.querySelector("a[href],button,input,select,textarea,[role=button],[role=link],[onclick],[tabindex],[contenteditable]"); }
    catch (e) { return true; }
  }

  function snapshot(o) {
    o = o || {};
    if (o.next > next) next = o.next;
    prune();
    var cap = Math.min(Math.max(o.max || 1200, 40), 6000);
    var lines = [], count = 0, visits = 0, truncated = false, focused = deepActive();
    var root = document.body || document.documentElement;
    if (o.scope) {
      var f = lookup(o.scope);
      if (!f.el) return fail(f);
      root = f.el;
    }

    function emit(d, s) {
      if (lines.length >= cap) { truncated = true; return false; }
      lines.push(indent(d) + "- " + s);
      return true;
    }

    function flush(d, buf) {
      if (!buf.length) return;
      var t = norm(buf.join(""));
      buf.length = 0;
      if (t && /[\p{L}\p{N}]/u.test(t)) emit(d, "text " + q(clip(t, 300)));
    }

    function rowLine(tr) {
      var cells = [];
      for (var i = 0; i < tr.cells.length && i < 16; i++) cells.push(clip(tr.cells[i].innerText, 60));
      return cells.join(" | ");
    }

    function visit(node, d, buf, inAct, inPtr, seen) {
      var c = kids(node);
      for (var i = 0; i < c.length; i++) {
        if (truncated) return;
        var n = c[i];
        if (n.nodeType === 3) { if (!inAct && seen) buf.push(n.nodeValue); continue; }
        if (n.nodeType !== 1) continue;
        if (++visits > 60000) { truncated = true; return; }
        var t = tagOf(n);
        if (SKIP[t]) continue;
        if (t === "br") { buf.push(" "); continue; }
        var st = css(n);
        if (!st || st.display === "none") continue;
        if (attr(n, "aria-hidden") === "true" && !(focused && n.contains(focused))) continue;
        if (n.hasAttribute("inert")) continue;
        var hidden = st.visibility === "hidden" || st.visibility === "collapse";
        var r = n.getBoundingClientRect();
        var sized = r.width > 0 && r.height > 0;
        var role = roleOf(n);
        var ptr = st.cursor === "pointer";
        var act = !hidden && sized && actionable(n, role, st, inPtr);
        var frame = t === "iframe" || t === "frame";
        var fdoc = frame ? frameDoc(n) : null;
        var big = r.width >= 40 && r.height >= 40;
        var canvasLike = (t === "canvas" || (frame && !fdoc)) && big;
        var name = "";
        var shown = role || (act ? "clickable" : "");
        if (!hidden && sized && (act || STRUCTURAL[role] || canvasLike || frame)) name = nameOf(n, shown);
        var show = !hidden && sized && (act || canvasLike || frame ||
          (STRUCTURAL[role] && (role !== "img" || name) && (role !== "group" || name)));
        var inline = !show && !frame && (st.display === "inline" || st.display === "contents" || st.display.indexOf("inline") === 0);
        if (inline) { visit(n, d, buf, inAct, inPtr || ptr, !hidden); continue; }

        if (!inAct) flush(d, buf);
        if (!show) {
          var inner = [];
          visit(n, d, inner, inAct, inPtr || ptr, !hidden);
          if (!inAct) flush(d, inner);
          continue;
        }

        // A data row with nothing to act on reads as one line: "cell | cell | cell".
        if (role === "row" && t === "tr" && !hasInteractive(n)) {
          var rl = rowLine(n);
          if (norm(rl)) emit(d, "row " + q(clip(rl, 300)));
          continue;
        }

        var parts = [role || (canvasLike ? "canvas" : "clickable")];
        if (act && !role) parts[0] = "clickable";
        if (name) parts.push(q(name));
        var fl = flagsOf(n, role, focused);
        for (var k = 0; k < fl.length; k++) parts.push(fl[k]);
        if (frame && !fdoc) parts.push("[cross-origin]");
        if (role === "link") { var u = urlOf(n); if (u) parts.push(u); }
        if (frame && !fdoc && n.src) parts.push("(" + clip(n.src, 90) + ")");
        var v = valueOf(n, role);
        if (v) parts.push(v);
        if (t === "select") { var op = optionsOf(n); if (op) parts.push(op); }
        if (act || canvasLike) { parts.push("[ref=" + refOf(n) + "]"); count++; }
        else if (CONTAINER_REF[role]) parts.push("[ref=" + refOf(n) + "]");
        if (!emit(d, parts.join(" "))) return;

        if (t === "select" || canvasLike) continue;
        if (frame) {
          if (fdoc && fdoc.body) { var fb = []; visit(fdoc.body, d + 1, fb, false, false, true); flush(d + 1, fb); }
          continue;
        }
        var own = [];
        var skipText = inAct || act || role === "heading" || role === "img";
        visit(n, d + 1, own, skipText, inPtr || ptr, !hidden);
        if (!skipText) flush(d + 1, own);
      }
    }

    var top = [];
    var rs = css(root);
    visit(root, 0, top, false, false, !rs || rs.visibility === "visible");
    flush(0, top);
    var se = document.scrollingElement || document.documentElement;
    return {
      ok: true, url: location.href, title: document.title || "", lines: lines, count: count, next: next,
      truncated: truncated, scrollY: Math.round(se ? se.scrollTop : 0), scrollHeight: Math.round(se ? se.scrollHeight : 0),
      viewportHeight: innerHeight, viewportWidth: innerWidth
    };
  }

  // ============================ Acting on handles ============================
  function frameOffset(el) {
    var x = 0, y = 0, w = win(el), guard = 0;
    try {
      while (w && w !== window && guard++ < 20) {
        var fe = w.frameElement;
        if (!fe) break;
        var r = fe.getBoundingClientRect(), s = css(fe);
        x += r.left + fe.clientLeft + (s ? parseFloat(s.paddingLeft) || 0 : 0);
        y += r.top + fe.clientTop + (s ? parseFloat(s.paddingTop) || 0 : 0);
        w = w.parent;
      }
    } catch (e) { /* a cross-origin parent: best effort */ }
    return { x: x, y: y };
  }

  function boxOf(el) {
    var r = el.getBoundingClientRect(), o = frameOffset(el);
    return { x: r.left + o.x, y: r.top + o.y, w: r.width, h: r.height, ox: o.x, oy: o.y };
  }

  function hitAt(el, x, y) {
    var h = el.ownerDocument.elementFromPoint(x, y), guard = 0;
    while (h && h.shadowRoot && guard++ < 20) {
      var inner = h.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === h) break;
      h = inner;
    }
    return h;
  }

  // What takes a click at x, y (viewport of the top page) before it reaches the frame the element
  // sits in: a layer in a parent document over the frame, or null when the way is clear.
  function frameCover(el, x, y) {
    var w = win(el);
    try {
      for (var guard = 0; w && w !== window && guard < 20; guard++) {
        var fe = w.frameElement;
        if (!fe) return null;
        var o = frameOffset(fe);
        var h = hitAt(fe, x - o.x, y - o.y);
        if (h && h !== fe) return h;
        w = w.parent;
      }
    } catch (e) { /* a parent from another site: best effort */ }
    return null;
  }

  function within(el, h) {
    for (var n = h, guard = 0; n && guard++ < 500; n = n.parentNode || n.host) if (n === el) return true;
    var lab = h.closest ? h.closest("label") : null;
    return !!(lab && (lab.control === el || lab.contains(el)));
  }

  function brief(el) {
    var role = roleOf(el) || tagOf(el), name = nameOf(el, role);
    if (!name) {
      // A covering layer is often a wrapper: name it after the dialog or region it belongs to.
      var host = el.closest ? el.closest("[role=dialog],[role=alertdialog],dialog,[aria-modal=true],[aria-label]") : null;
      if (host) { role = roleOf(host) || tagOf(host); name = nameOf(host, role); }
    }
    if (!name) name = textOf(el, 60);
    return role + (name ? " " + q(name) : "");
  }

  function describe(el) {
    var role = roleOf(el);
    if (!role && actionable(el, "", css(el), false)) role = "clickable";
    role = role || tagOf(el);
    return { tag: tagOf(el), role: role, name: nameOf(el, role) };
  }

  function resolve(ref, o) {
    o = o || {};
    var f = lookup(ref);
    if (!f.el) return fail(f);
    var el = f.el;
    if (o.scroll !== false) {
      try { el.scrollIntoView({ block: "center", inline: "center", behavior: "instant" }); } catch (e) { try { el.scrollIntoView(); } catch (e2) { } }
    }
    var b = boxOf(el), d = describe(el), vw = innerWidth, vh = innerHeight;
    var out = {
      ok: true, x: b.x, y: b.y, w: b.w, h: b.h, tag: d.tag, role: d.role, name: d.name,
      disabled: !!(el.disabled || attr(el, "aria-disabled") === "true"),
      editable: !!(el.isContentEditable || ((d.tag === "input" || d.tag === "textarea") && !el.readOnly)),
      type: (attr(el, "type") || "").toLowerCase(), checked: checkedOf(el, d.role), multiple: !!el.multiple,
      cx: b.x + b.w / 2, cy: b.y + b.h / 2
    };
    if (b.w <= 0 || b.h <= 0) { out.hidden = true; return out; }
    if (o.hit === false) return out;
    var pts = [[0.5, 0.5], [0.25, 0.5], [0.75, 0.5], [0.5, 0.25], [0.5, 0.75], [0.15, 0.15], [0.85, 0.85]];
    var cover = null;
    for (var i = 0; i < pts.length; i++) {
      var x = b.x + b.w * pts[i][0], y = b.y + b.h * pts[i][1];
      if (x < 0 || y < 0 || x >= vw || y >= vh) continue;
      var h = hitAt(el, x - b.ox, y - b.oy);
      if (h && within(el, h)) {
        // In a frame, the point must also reach the frame through every page around it.
        var over = frameCover(el, x, y);
        if (!over) { out.cx = x; out.cy = y; return out; }
        h = over;
      }
      if (h && !cover) cover = h;
    }
    out.cx = Math.min(Math.max(out.cx, 1), vw - 1);
    out.cy = Math.min(Math.max(out.cy, 1), vh - 1);
    if (cover) out.covered = brief(cover); else out.offscreen = true;
    return out;
  }

  function focusTarget(el) {
    if (el.matches && el.matches("input,textarea,select,[contenteditable],[contenteditable=true],[tabindex]")) return el;
    var inner = el.querySelector ? el.querySelector("input:not([type=hidden]),textarea,[contenteditable=''],[contenteditable=true]") : null;
    return inner || el;
  }

  // Focuses the field and selects its text, so typed text replaces what was there.
  function prepareType(ref, o) {
    var f = lookup(ref);
    if (!f.el) return fail(f);
    var el = focusTarget(f.el);
    try { el.scrollIntoView({ block: "center", behavior: "instant" }); } catch (e) { }
    try { el.focus({ preventScroll: true }); } catch (e) { }
    var t = tagOf(el), had = "";
    if (o && o.replace) {
      if (t === "input" || t === "textarea") {
        had = el.value || "";
        try { el.select(); } catch (e) { try { el.setSelectionRange(0, had.length); } catch (e2) { } }
      } else if (el.isContentEditable) {
        had = el.textContent || "";
        var s = win(el).getSelection(), r = el.ownerDocument.createRange();
        r.selectNodeContents(el); s.removeAllRanges(); s.addRange(r);
      }
    } else if (t === "input" || t === "textarea") {
      try { var len = (el.value || "").length; el.setSelectionRange(len, len); } catch (e) { }
    }
    var active = deepActive();
    var d = describe(el);
    return {
      ok: true, focused: active === el || (active && el.contains && el.contains(active)), had: had.length > 0,
      tag: d.tag, role: d.role, name: d.name, disabled: !!el.disabled, readonly: !!el.readOnly,
      type: String(el.type || attr(el, "type") || "").toLowerCase(),
      editable: !!(el.isContentEditable || t === "input" || t === "textarea")
    };
  }

  function valueNow(ref) {
    var f = lookup(ref);
    if (!f.el) return fail(f);
    var el = focusTarget(f.el), t = tagOf(el);
    if ((el.type || "").toLowerCase() === "password") return { ok: true, value: el.value ? "(filled)" : "" };
    return { ok: true, value: t === "input" || t === "textarea" || t === "select" ? el.value : el.isContentEditable ? el.textContent : "" };
  }

  function selectOptions(ref, values) {
    var f = lookup(ref);
    if (!f.el) return fail(f);
    var el = f.el;
    if (tagOf(el) !== "select") {
      var inner = el.querySelector ? el.querySelector("select") : null;
      if (!inner) return { ok: false, error: "not a native <select>. For a custom dropdown, click it to open it, then click the option" };
      el = inner;
    }
    if (el.disabled) return { ok: false, error: "the dropdown is disabled" };
    var want = (values || []).map(function (v) { return norm(v).toLowerCase(); });
    var opts = Array.prototype.slice.call(el.options);
    var hits = opts.filter(function (o) {
      return want.indexOf(norm(o.value).toLowerCase()) >= 0 || want.indexOf(norm(o.label).toLowerCase()) >= 0 || want.indexOf(norm(o.text).toLowerCase()) >= 0;
    });
    if (!hits.length) hits = opts.filter(function (o) {
      var tx = norm(o.text).toLowerCase();
      return want.some(function (w) { return w && tx.indexOf(w) >= 0; });
    });
    if (!el.multiple) hits = hits.slice(0, 1);
    if (!hits.length) return { ok: false, error: "no option matches", options: opts.slice(0, 40).map(function (o) { return norm(o.text); }) };
    try { el.focus({ preventScroll: true }); } catch (e) { }
    opts.forEach(function (o) { o.selected = hits.indexOf(o) >= 0; });
    el.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
    return { ok: true, selected: hits.map(function (o) { return norm(o.text); }) };
  }

  function element(ref) { var f = lookup(ref); return f.el || null; }

  function box(ref, o) {
    var f = lookup(ref);
    if (!f.el) return fail(f);
    if (o && o.scroll) { try { f.el.scrollIntoView({ block: "center", inline: "center", behavior: "instant" }); } catch (e) { } }
    var b = boxOf(f.el);
    return { ok: true, x: b.x, y: b.y, w: b.w, h: b.h, vw: innerWidth, vh: innerHeight, dpr: devicePixelRatio };
  }

  function scrollFallback(o) {
    o = o || {};
    var target = null;
    if (o.ref) { var f = lookup(o.ref); if (!f.el) return fail(f); target = f.el; }
    var se = document.scrollingElement || document.documentElement;
    if (o.to === "top") { (target || se).scrollTo(0, 0); }
    else if (o.to === "bottom") { (target || se).scrollTo(0, (target || se).scrollHeight); }
    else (target || window).scrollBy(o.dx || 0, o.dy || 0);
    return { ok: true, y: Math.round(se.scrollTop), height: Math.round(se.scrollHeight), vh: innerHeight };
  }

  function scrollPos() {
    var se = document.scrollingElement || document.documentElement;
    return { ok: true, x: Math.round(se.scrollLeft), y: Math.round(se.scrollTop), height: Math.round(se.scrollHeight), width: Math.round(se.scrollWidth), vw: innerWidth, vh: innerHeight, dpr: devicePixelRatio };
  }

  function hasText(needle) {
    var b = document.body;
    var t = (b ? b.innerText : "") || "";
    return { ok: true, found: t.toLowerCase().indexOf(String(needle).toLowerCase()) >= 0 };
  }

  // ============================ Reading ============================
  // Menus, toolbars, sidebars and the page's own header and footer. A header inside an article is
  // content (it holds the title), so it is judged by its role, not its tag.
  var CHROME_ROLE = { navigation: 1, complementary: 1, banner: 1, contentinfo: 1 };
  function chrome(n) { return attr(n, "aria-hidden") === "true" || !!CHROME_ROLE[roleOf(n)]; }

  function readable(o) {
    o = o || {};
    var root = null;
    if (o.ref) { var f = lookup(o.ref); if (!f.el) return fail(f); root = f.el; }
    var main = !root && !o.all ? document.querySelector("main,[role=main],article") : null;
    root = root || main || document.body;
    if (!root) return { ok: true, title: document.title || "", text: "" };
    var skipChrome = !o.ref && !o.all;
    var text = o.format === "markdown" ? markdown(root, skipChrome) : plain(root, skipChrome);
    return { ok: true, title: document.title || "", url: location.href, text: text };
  }

  function plain(root, skipChrome) {
    var full = root.innerText || "";
    if (skipChrome) {
      // innerText needs layout, so read the live page and leave out the chrome by subtracting it.
      var parts = root.querySelectorAll("nav,aside,header,footer,[role],[aria-hidden=true]");
      for (var i = 0; i < parts.length; i++) {
        if (!chrome(parts[i])) continue;
        var t = parts[i].innerText;
        if (t && t.length > 20) full = full.replace(t, "");
      }
    }
    return full.replace(/\n{3,}/g, "\n\n").trim();
  }

  function markdown(root, skipChrome) {
    var out = [];
    function inline(s) { return s.replace(/\s+/g, " "); }
    function walk(node, listDepth, inPre) {
      var c = kids(node);
      for (var i = 0; i < c.length; i++) {
        var n = c[i];
        if (n.nodeType === 3) { out.push(inPre ? n.nodeValue : inline(n.nodeValue)); continue; }
        if (n.nodeType !== 1) continue;
        var t = tagOf(n);
        if (SKIP[t] || t === "svg" || t === "button" || t === "select" || t === "input" || t === "textarea") continue;
        if (skipChrome && chrome(n)) continue;
        var st = css(n);
        if (!st || st.display === "none" || st.visibility === "hidden") continue;
        var h = /^h([1-6])$/.exec(t);
        if (h) { out.push("\n\n" + "######".slice(0, +h[1]) + " " + clip(n.innerText, 200) + "\n\n"); continue; }
        switch (t) {
          case "a":
            var label = clip(n.innerText, 160), href = n.href && typeof n.href === "string" && !/^javascript:/i.test(n.href) ? n.href : "";
            out.push(label ? (href ? "[" + label + "](" + href + ")" : label) : "");
            continue;
          case "img":
            var alt = attr(n, "alt");
            if (alt) out.push("![" + clip(alt, 100) + "]");
            continue;
          case "pre":
            out.push("\n\n```\n" + (n.innerText || "").replace(/\n+$/, "") + "\n```\n\n");
            continue;
          case "code":
            out.push(inPre ? n.textContent : "`" + clip(n.innerText, 200) + "`");
            continue;
          case "strong": case "b":
            var bt = clip(n.innerText, 200); if (bt) out.push(" **" + bt + "** ");
            continue;
          case "em": case "i":
            var it = clip(n.innerText, 200); if (it) out.push(" *" + it + "* ");
            continue;
          case "br": out.push("\n"); continue;
          case "hr": out.push("\n\n---\n\n"); continue;
          case "li":
            out.push("\n" + "  ".repeat(Math.max(0, listDepth - 1)) + (n.parentElement && tagOf(n.parentElement) === "ol" ? "1. " : "- "));
            walk(n, listDepth, inPre);
            continue;
          case "ul": case "ol":
            walk(n, listDepth + 1, inPre); out.push("\n");
            continue;
          case "blockquote":
            out.push("\n\n> " + clip(n.innerText, 1200).replace(/\n/g, "\n> ") + "\n\n");
            continue;
          case "table":
            out.push("\n\n" + table(n) + "\n\n");
            continue;
          case "iframe": case "frame":
            var fd = frameDoc(n);
            if (fd && fd.body) walk(fd.body, listDepth, inPre);
            continue;
        }
        var block = st.display && st.display.indexOf("inline") !== 0 && st.display !== "contents";
        if (block) out.push("\n");
        walk(n, listDepth, inPre || t === "pre");
        if (block) out.push("\n");
      }
    }
    walk(root, 0, false);
    return out.join("").replace(/[ \t]+\n/g, "\n").replace(/\n[ \t]+(?=\S)/g, function (m) { return m.indexOf("-") >= 0 ? m : "\n"; })
      .replace(/\n{3,}/g, "\n\n").replace(/[ \t]{2,}/g, " ").trim();
  }

  function table(t) {
    var rows = [], max = 0;
    for (var i = 0; i < t.rows.length && i < 200; i++) {
      var r = [];
      for (var j = 0; j < t.rows[i].cells.length && j < 20; j++) r.push(clip(t.rows[i].cells[j].innerText, 80).replace(/\|/g, "/"));
      max = Math.max(max, r.length);
      rows.push(r);
    }
    if (!rows.length) return "";
    var lines = rows.map(function (r) { while (r.length < max) r.push(""); return "| " + r.join(" | ") + " |"; });
    var sep = "|" + new Array(max + 1).join(" --- |");
    lines.splice(1, 0, sep);
    return lines.join("\n");
  }

  // ============================ Entry ============================
  globalThis.__pda = {
    v: 3,
    element: element,
    call: function (op, arg) {
      try {
        switch (op) {
          case "snapshot": return snapshot(arg);
          case "resolve": return resolve(arg.ref, arg);
          case "prepareType": return prepareType(arg.ref, arg);
          case "value": return valueNow(arg.ref);
          case "select": return selectOptions(arg.ref, arg.values);
          case "box": return box(arg.ref, arg);
          case "scroll": return scrollFallback(arg);
          case "scrollPos": return scrollPos();
          case "read": return readable(arg);
          case "hasText": return hasText(arg.text);
          case "describe": var f = lookup(arg.ref); return f.el ? Object.assign({ ok: true }, describe(f.el)) : fail(f);
          default: return { ok: false, error: "unknown op " + op };
        }
      } catch (e) {
        return { ok: false, error: String((e && e.message) || e) };
      }
    }
  };
})();
