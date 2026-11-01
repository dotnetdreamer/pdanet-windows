// PdaNet browser MCP — in-page helper.
//
// Injected into the active page and called by the C# BrowserSession. It gives the agent its
// "eyes" (a DOM-derived accessibility-style tree where every actionable element carries a
// [ref=eGEN-N] handle) and resolves those handles back to live elements for clicks and typing.
//
// The tree descends into open shadow roots and same-origin iframes, and resolve() adds the
// enclosing frame offsets so a click on an in-frame element still lands at the right screen
// point. Each snapshot bumps window.__pdaGen and stamps that generation into every handle, so a
// handle from an earlier snapshot is reported stale rather than silently hitting whatever now
// sits at that index. Nothing here ever emits the value of a password field.
(function () {
  "use strict";

  function viewOf(el) { try { return (el.ownerDocument && el.ownerDocument.defaultView) || window; } catch (e) { return window; } }

  function clip(s, n) {
    s = (s == null ? "" : String(s)).replace(/\s+/g, " ").trim();
    return s.length > n ? s.slice(0, n - 1) + "…" : s;
  }

  function visible(el) {
    if (!el || el.nodeType !== 1) return false;
    var s;
    try { s = viewOf(el).getComputedStyle(el); } catch (e) { return false; }
    if (!s || s.visibility === "hidden" || s.display === "none" || parseFloat(s.opacity) === 0) return false;
    var r = el.getBoundingClientRect();
    if (r.width <= 0 && r.height <= 0) return false;
    return true;
  }

  function sensitive(el) {
    return el.tagName.toLowerCase() === "input" && /^password$/i.test(el.getAttribute("type") || "");
  }

  function accName(el) {
    var n = el.getAttribute("aria-label");
    if (!n && el.getAttribute("aria-labelledby")) {
      n = el.getAttribute("aria-labelledby").split(/\s+/).map(function (id) {
        var e = (el.ownerDocument || document).getElementById(id);
        return e ? e.innerText : "";
      }).join(" ");
    }
    var tag = el.tagName.toLowerCase();
    if (!n) {
      if (tag === "input" || tag === "textarea") {
        if (el.labels && el.labels.length) n = el.labels[0].innerText;
        // Never fall back to el.value for a name — it can contain a password or other secret.
        n = n || el.getAttribute("placeholder") || el.getAttribute("name") || el.getAttribute("title") || "";
      } else if (tag === "img") {
        n = el.getAttribute("alt") || "";
      } else if (tag === "a" || tag === "button" || /^h[1-6]$/.test(tag) || el.getAttribute("role")) {
        n = el.innerText || el.textContent || "";
      }
    }
    return clip(n, 120);
  }

  var TAG_ROLE = {
    a: "link", button: "button", select: "combobox", textarea: "textbox",
    nav: "navigation", main: "main", header: "banner", footer: "contentinfo",
    form: "form", ul: "list", ol: "list", li: "listitem", table: "table",
    article: "article", section: "region", img: "img", summary: "button"
  };

  function roleOf(el) {
    var explicit = el.getAttribute("role");
    if (explicit) return explicit.split(/\s+/)[0];
    var tag = el.tagName.toLowerCase();
    if (/^h[1-6]$/.test(tag)) return "heading";
    if (tag === "input") {
      var t = (el.getAttribute("type") || "text").toLowerCase();
      if (t === "checkbox") return "checkbox";
      if (t === "radio") return "radio";
      if (t === "submit" || t === "button" || t === "reset" || t === "image") return "button";
      if (t === "range") return "slider";
      if (t === "hidden") return "";
      return "textbox";
    }
    return TAG_ROLE[tag] || "";
  }

  var ACTIONABLE = {
    link: 1, button: 1, textbox: 1, combobox: 1, checkbox: 1, radio: 1, slider: 1,
    menuitem: 1, menuitemcheckbox: 1, menuitemradio: 1, tab: 1, switch: 1, option: 1,
    searchbox: 1, spinbutton: 1
  };

  var STRUCTURAL = {
    navigation: 1, main: 1, banner: 1, contentinfo: 1, form: 1, list: 1, listitem: 1,
    table: 1, article: 1, region: 1, heading: 1, dialog: 1, tablist: 1, menu: 1
  };

  function actionable(el, role) {
    if (ACTIONABLE[role]) return true;
    if (el.getAttribute("contenteditable") === "true") return true;
    if (el.hasAttribute("onclick")) return true;
    var ti = el.getAttribute("tabindex");
    if (ti != null && ti !== "-1") return true;
    return false;
  }

  function state(el, role) {
    var bits = [];
    if (el.disabled || el.getAttribute("aria-disabled") === "true") bits.push("disabled");
    if (role === "checkbox" || role === "radio" || role === "switch") {
      var on = el.checked != null ? el.checked : el.getAttribute("aria-checked") === "true";
      bits.push(on ? "checked" : "unchecked");
    }
    if (el.getAttribute("aria-expanded")) bits.push(el.getAttribute("aria-expanded") === "true" ? "expanded" : "collapsed");
    if (el.getAttribute("aria-selected") === "true") bits.push("selected");
    if (role === "textbox" || role === "searchbox") {
      if (sensitive(el)) { if (el.value) bits.push("(filled)"); }   // never reveal a password's value
      else if (el.value) bits.push('value="' + clip(el.value, 60) + '"');
    }
    return bits;
  }

  function snapshot() {
    var gen = (window.__pdaGen = (window.__pdaGen || 0) + 1);
    var els = (window.__pdaEls = []);
    var lines = [];
    var cap = 1500;

    function pre(d) { var s = ""; for (var i = 0; i < d; i++) s += "  "; return s; }

    function walk(node, depth) {
      if (lines.length >= cap || !node) return;
      for (var i = 0; i < node.childNodes.length; i++) {
        var c = node.childNodes[i];
        if (c.nodeType === 3) {
          var txt = clip(c.textContent, 100);
          if (txt && !/^[\s•\-|·]+$/.test(txt) && node.childElementCount === 0) {
            var pr = roleOf(node.nodeType === 1 ? node : c.parentElement || document.body);
            if (!ACTIONABLE[pr] && pr !== "heading") lines.push(pre(depth) + 'text "' + txt + '"');
          }
          continue;
        }
        if (c.nodeType !== 1) continue;
        var tag = c.tagName.toLowerCase();
        if (tag === "script" || tag === "style" || tag === "noscript" || tag === "svg" || tag === "template") continue;
        if (!visible(c)) continue;

        var role = roleOf(c);
        var act = actionable(c, role);
        var emit = act || STRUCTURAL[role] || role === "img";
        var childDepth = depth;

        if (emit) {
          var name = accName(c);
          var parts = ["- " + (role || tag)];
          if (name) parts.push('"' + name + '"');
          var st = state(c, role);
          for (var k = 0; k < st.length; k++) parts.push(st[k]);
          if (act) {
            els.push(c);
            parts.push("[ref=e" + gen + "-" + els.length + "]");
          }
          lines.push(pre(depth) + parts.join(" "));
          childDepth = depth + 1;
        }

        walk(c, childDepth);
        if (c.shadowRoot) walk(c.shadowRoot, childDepth);        // open shadow DOM
        if (tag === "iframe" || tag === "frame") {
          try { var d = c.contentDocument; if (d && d.body) walk(d.body, childDepth); } catch (e) { /* cross-origin */ }
        }
      }
    }

    if (document.body) walk(document.body, 0);
    var dialogs = window.__pdaDialogs || [];
    window.__pdaDialogs = [];
    return {
      ok: true, url: location.href, title: document.title || "",
      text: lines.join("\n"), count: els.length, truncated: lines.length >= cap, dialogs: dialogs
    };
  }

  function elFor(ref) {
    var els = window.__pdaEls, m = /^e(\d+)-(\d+)$/.exec(String(ref));
    if (!m) return { missing: true };
    if (!els || parseInt(m[1], 10) !== window.__pdaGen) return { stale: true };
    var el = els[parseInt(m[2], 10) - 1];
    if (!el) return { missing: true };
    if (!el.isConnected) return { stale: true };   // works for shadow DOM and iframes too
    return { el: el };
  }

  // Offset of the element's frame chain, so in-iframe coordinates map to the top viewport.
  function frameOffset(el) {
    var off = { x: 0, y: 0 };
    try {
      var w = viewOf(el);
      while (w && w !== window.top) {
        var fe = w.frameElement; if (!fe) break;
        var r = fe.getBoundingClientRect(); off.x += r.left; off.y += r.top; w = w.parent;
      }
    } catch (e) { /* cross-origin frame: best effort */ }
    return off;
  }

  function rectOf(el) {
    try { el.scrollIntoView({ block: "center", inline: "center" }); } catch (e) { }
    var r = el.getBoundingClientRect(), o = frameOffset(el);
    var x = r.left + o.x, y = r.top + o.y;
    return {
      ok: true, x: x, y: y, w: r.width, h: r.height, cx: x + r.width / 2, cy: y + r.height / 2,
      tag: el.tagName.toLowerCase(), type: (el.getAttribute("type") || "").toLowerCase(),
      name: accName(el),
      inView: (y + r.height) > 0 && (x + r.width) > 0 && y < innerHeight && x < innerWidth
    };
  }

  function resolve(ref) {
    var f = elFor(ref);
    if (f.stale) return { ok: false, stale: true };
    if (f.missing || !f.el) return { ok: false, missing: true };
    return rectOf(f.el);
  }

  function focus(ref) {
    var f = elFor(ref);
    if (!f.el) return { ok: false, stale: !!f.stale, missing: !!f.missing };
    try { f.el.scrollIntoView({ block: "center" }); f.el.focus(); } catch (e) { }
    return rectOf(f.el);
  }

  function clearField(ref) {
    var f = elFor(ref);
    if (!f.el) return { ok: false, stale: !!f.stale, missing: !!f.missing };
    var el = f.el;
    try {
      el.focus();
      if (el.value != null) {
        el.value = "";
        el.dispatchEvent(new Event("input", { bubbles: true }));
        el.dispatchEvent(new Event("change", { bubbles: true }));
      } else if (el.isContentEditable) {
        el.textContent = "";
      }
    } catch (e) { return { ok: false, error: String(e) }; }
    return { ok: true };
  }

  function selectOptions(ref, values) {
    var f = elFor(ref);
    if (!f.el) return { ok: false, stale: !!f.stale, missing: !!f.missing };
    var el = f.el;
    if (el.tagName.toLowerCase() !== "select") return { ok: false, error: "not a <select>" };
    var want = {};
    for (var i = 0; i < values.length; i++) want[String(values[i]).toLowerCase()] = 1;
    var chosen = [];
    for (var j = 0; j < el.options.length; j++) {
      var o = el.options[j];
      var hit = want[(o.value || "").toLowerCase()] || want[(o.text || "").toLowerCase()] || want[(o.label || "").toLowerCase()];
      o.selected = !!hit;
      if (hit) chosen.push(o.text || o.value);
    }
    try { el.focus(); } catch (e) { }
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
    return { ok: chosen.length > 0, selected: chosen };
  }

  function scroll(arg) {
    if (arg && arg.ref) {
      var f = elFor(arg.ref);
      if (f.el) { try { f.el.scrollIntoView({ block: "center" }); } catch (e) { } }
    } else {
      var by = (arg && arg.amount) || Math.round(innerHeight * 0.85);
      var dir = arg && arg.dir;
      if (dir === "up") scrollBy(0, -by);
      else if (dir === "left") scrollBy(-by, 0);
      else if (dir === "right") scrollBy(by, 0);
      else if (dir === "top") scrollTo(0, 0);
      else if (dir === "bottom") scrollTo(0, document.body ? document.body.scrollHeight : 0);
      else scrollBy(0, by);
    }
    return { ok: true, x: Math.round(scrollX), y: Math.round(scrollY) };
  }

  function readable(format) {
    var root = document.querySelector("main, article, [role=main]") || document.body;
    if (!root) return { ok: true, title: document.title || "", text: "" };
    if (format !== "markdown") {
      return { ok: true, title: document.title || "", text: clipLong(root.innerText || "") };
    }
    var out = [];
    (function walk(node) {
      for (var i = 0; i < node.childNodes.length; i++) {
        var c = node.childNodes[i];
        if (c.nodeType === 3) { var t = c.textContent.replace(/\s+/g, " "); if (t.trim()) out.push(t); continue; }
        if (c.nodeType !== 1) continue;
        var tag = c.tagName.toLowerCase();
        if (tag === "script" || tag === "style" || tag === "noscript" || !visible(c)) continue;
        var h = /^h([1-6])$/.exec(tag);
        if (h) { out.push("\n\n" + new Array(+h[1] + 1).join("#") + " " + clip(c.innerText, 200) + "\n"); continue; }
        if (tag === "a" && c.href) { out.push("[" + clip(c.innerText, 120) + "](" + c.href + ")"); continue; }
        if (tag === "li") { out.push("\n- "); walk(c); continue; }
        if (tag === "p" || tag === "br" || tag === "div" || tag === "tr") { out.push("\n"); walk(c); out.push("\n"); continue; }
        if (tag === "strong" || tag === "b") { out.push(" **" + clip(c.innerText, 200) + "** "); continue; }
        if (tag === "em" || tag === "i") { out.push(" *" + clip(c.innerText, 200) + "* "); continue; }
        if (tag === "code") { out.push(" `" + clip(c.innerText, 200) + "` "); continue; }
        walk(c);
      }
    })(root);
    var md = out.join("").replace(/\n{3,}/g, "\n\n").replace(/[ \t]{2,}/g, " ").trim();
    return { ok: true, title: document.title || "", text: clipLong(md) };
  }

  function clipLong(s) {
    s = s || "";
    var max = 24000;
    return s.length > max ? s.slice(0, max) + "\n… [truncated, " + s.length + " characters total]" : s;
  }

  function hasText(needle) {
    var t = (document.body ? document.body.innerText : "") || "";
    return { ok: true, found: t.toLowerCase().indexOf(String(needle).toLowerCase()) >= 0 };
  }

  // Read and clear any JavaScript dialogs the page tried to open (see the override script).
  function drainDialogs() {
    var d = window.__pdaDialogs || [];
    window.__pdaDialogs = [];
    return { ok: true, dialogs: d };
  }

  function setDialogPolicy(arg) {
    window.__pdaDialogPolicy = { accept: !!(arg && arg.accept), text: arg && arg.text != null ? String(arg.text) : null };
    var d = window.__pdaDialogs || [];
    window.__pdaDialogs = [];
    return { ok: true, dialogs: d };
  }

  window.__pdaDispatch = function (op, arg) {
    try {
      switch (op) {
        case "snapshot": return snapshot();
        case "resolve": return resolve(arg);
        case "focus": return focus(arg);
        case "clear": return clearField(arg);
        case "select": return selectOptions(arg.ref, arg.values || []);
        case "scroll": return scroll(arg || {});
        case "read": return readable(arg || "text");
        case "hasText": return hasText(arg || "");
        case "dialogs": return drainDialogs();
        case "dialogPolicy": return setDialogPolicy(arg || {});
        default: return { ok: false, error: "unknown op " + op };
      }
    } catch (e) {
      return { ok: false, error: String(e && e.message || e) };
    }
  };
})();
