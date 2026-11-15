namespace RhapsodyServer.Core.Profiling;

/// <summary>
/// Template hints: a page overlay that shows which template file produced each part of the page.
/// The framework marks every template with HTML comments (see Rhapsody's TemplateHintsExtension); this script reads them
/// back and draws a labelled border per template. It is loaded by the browser probe, only on sites with hints switched on.
/// </summary>
public static class TemplateHintsOverlay
{
    /// <summary>The environment variable the collector sets for hinted sites, and the framework reads.</summary>
    public const string EnvFlag = "RHAPSODY_TEMPLATE_HINTS";
    public const string ScriptPath = "/__rs/hints.js";

    public const string Script = """
        (function () {
          if (window.__rsHints) return;
          window.__rsHints = true;
          var STORE = 'rhapsody-server.hints';
          var state = { on: true, blocks: true };
          try { var saved = JSON.parse(localStorage.getItem(STORE) || '{}'); if (saved.on === false) state.on = false; if (saved.blocks === false) state.blocks = false; } catch (e) {}
          function save() { try { localStorage.setItem(STORE, JSON.stringify(state)); } catch (e) {} }

          var COLORS = ['#7c5cff', '#0d9488', '#d97706', '#dc2626', '#2563eb', '#db2777', '#16a34a', '#9333ea'];
          var pairs = [], host, root, layer, panel, count, timer;

          // The framework wraps each template in <!--rs:tpl {...}--> comments. Read them back into open/close pairs.
          function collect() {
            pairs = [];
            var stack = [], w = document.createTreeWalker(document, NodeFilter.SHOW_COMMENT, null), c;
            while ((c = w.nextNode())) {
              var t = c.nodeValue;
              if (t.indexOf('rs:tpl ') !== 0) continue;
              var d; try { d = JSON.parse(t.slice(7)); } catch (e) { continue; }
              if (d.e === 'open') { var p = { open: c, close: null, path: String(d.p || ''), name: String(d.n || ''), kind: d.k === 'block' ? 'block' : 'template', ms: null, depth: stack.length }; stack.push(p); pairs.push(p); }
              else if (d.e === 'close') { var o = stack.pop(); if (o) { o.close = c; o.ms = typeof d.ms === 'number' ? d.ms : null; } }
            }
          }

          function boxOf(p) {
            if (!p.close || !p.open.parentNode || !p.close.parentNode) return null;
            var r = document.createRange();
            try { r.setStartAfter(p.open); r.setEndBefore(p.close); } catch (e) { return null; }
            var b = r.getBoundingClientRect();
            return b.width >= 2 && b.height >= 2 ? b : null;
          }

          function short(path, name) {
            var parts = path.split('/'); var tail = parts.slice(-2).join('/');
            return tail || name;
          }

          function el(tag, css, text) {
            var e = document.createElement(tag);
            if (css) e.style.cssText = css;
            if (text != null) e.textContent = text;
            return e;
          }

          function build() {
            host = document.createElement('div');
            host.setAttribute('data-rs-hints', '');
            host.style.cssText = 'all:initial;position:absolute;left:0;top:0;width:0;height:0;overflow:visible;z-index:2147483647;';
            root = host.attachShadow({ mode: 'closed' });
            layer = el('div', 'position:absolute;left:0;top:0;width:0;height:0;overflow:visible;');
            panel = el('div', 'position:fixed;left:12px;bottom:12px;font:12px/1.4 ui-monospace,Consolas,monospace;background:#111827;color:#f3f4f6;border:1px solid #374151;border-radius:8px;padding:6px 10px;box-shadow:0 4px 18px rgba(0,0,0,.35);display:flex;gap:12px;align-items:center;');
            var onLbl = el('label', 'display:flex;gap:6px;align-items:center;cursor:pointer;');
            var on = el('input'); on.type = 'checkbox'; on.checked = state.on;
            on.onchange = function () { state.on = on.checked; save(); draw(); };
            onLbl.append(on, el('span', '', 'Template hints'));
            var blkLbl = el('label', 'display:flex;gap:6px;align-items:center;cursor:pointer;color:#d1d5db;');
            var blk = el('input'); blk.type = 'checkbox'; blk.checked = state.blocks;
            blk.onchange = function () { state.blocks = blk.checked; save(); draw(); };
            blkLbl.append(blk, el('span', '', 'blocks'));
            count = el('span', 'color:#9ca3af;', '');
            panel.append(onLbl, blkLbl, count);
            root.append(layer, panel);
            document.documentElement.appendChild(host);
          }

          function draw() {
            layer.replaceChildren();
            var shown = 0;
            if (state.on) {
              var origin = host.getBoundingClientRect(), placed = [], seen = [];
              pairs.forEach(function (p, i) {
                if (p.kind === 'block' && !state.blocks) return;
                var b = boxOf(p); if (!b) return;
                // A child that fills exactly the same box as an ancestor is drawn slightly inside it, so both stay visible.
                var same = 0;
                seen.forEach(function (s) { if (Math.abs(s.left - b.left) < 1 && Math.abs(s.top - b.top) < 1 && Math.abs(s.width - b.width) < 1 && Math.abs(s.height - b.height) < 1) same++; });
                seen.push(b);
                var inset = same * 3, color = COLORS[p.depth % COLORS.length];
                var left = b.left - origin.left + inset, top = b.top - origin.top + inset;
                var w = Math.max(2, b.width - inset * 2), h = Math.max(2, b.height - inset * 2);
                var box = el('div', 'position:absolute;box-sizing:border-box;pointer-events:none;border:1px ' + (p.kind === 'block' ? 'dotted' : 'dashed') + ' ' + color + ';left:' + left + 'px;top:' + top + 'px;width:' + w + 'px;height:' + h + 'px;');
                var text = (p.kind === 'block' ? 'block ' + p.name + ' · ' : '') + short(p.path, p.name) + (p.ms != null ? ' · ' + p.ms + ' ms' : '');
                // Labels sit at the box's top-left; when one is already there, move down a row.
                var lw = text.length * 6.6 + 18, ly = top;
                for (var k = 0; k < placed.length; k++) { var q = placed[k]; if (ly < q.y + 16 && ly + 16 > q.y && left < q.x + q.w && left + lw > q.x) { ly = q.y + 16; k = -1; } }
                placed.push({ x: left, y: ly, w: lw });
                var label = el('div', 'position:absolute;pointer-events:auto;cursor:pointer;white-space:nowrap;max-width:60vw;overflow:hidden;text-overflow:ellipsis;font:11px/15px ui-monospace,Consolas,monospace;color:#fff;background:' + color + ';padding:0 6px;border-radius:0 0 4px 0;opacity:.92;left:' + left + 'px;top:' + (ly - top + top) + 'px;', text);
                label.title = p.path + '\nClick to copy the path';
                label.onmouseenter = function () { box.style.borderStyle = 'solid'; box.style.borderWidth = '2px'; box.style.background = color + '22'; label.style.opacity = '1'; };
                label.onmouseleave = function () { box.style.borderStyle = p.kind === 'block' ? 'dotted' : 'dashed'; box.style.borderWidth = '1px'; box.style.background = 'none'; label.style.opacity = '.92'; };
                label.onclick = function (ev) {
                  ev.preventDefault(); ev.stopPropagation();
                  var done = function (ok) { label.textContent = ok ? 'Copied: ' + p.path : 'Could not copy'; setTimeout(function () { label.textContent = text; }, 1400); };
                  try { navigator.clipboard.writeText(p.path).then(function () { done(true); }, function () { done(false); }); } catch (e) { done(false); }
                };
                layer.append(box, label);
                shown++;
              });
            }
            var total = pairs.filter(function (p) { return p.close; }).length;
            count.textContent = pairs.length ? (state.on ? shown + ' of ' + total + ' shown' : total + ' found') : 'no markers on this page';
          }

          function later() { clearTimeout(timer); timer = setTimeout(draw, 120); }

          function start() {
            collect();
            build();
            draw();
            addEventListener('resize', later);
            addEventListener('load', later);
            if (window.ResizeObserver) { try { new ResizeObserver(later).observe(document.documentElement); } catch (e) {} }
            if (document.fonts && document.fonts.ready) document.fonts.ready.then(later);
          }
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
        })();
        """;
}
