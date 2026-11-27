/* MathNotes: Funktionsgraphen aus LaTeX-Ausdruecken.
 *   MathPlot.parseFunctions("f(x)=x^2, g(x)=\\sin x")  -> [{name, ast}]
 *   MathPlot.graphSvg(text, props)                      -> SVG-String (wirft Error bei ungueltigem Ausdruck)
 *   MathPlot.graphToTikz(text, props)                   -> pgfplots/TikZ-Code (wirft Error)
 * props: {xmin,xmax,ymin,ymax} (null/fehlend = automatisch)
 * Gleicher Parser fuer Anzeige und TikZ, damit beide dasselbe meinen. */
(function (root) {
  'use strict';

  // ---------------------------------------------------------------- Tokenizer
  function tokenize(src) {
    var t = [], i = 0, n = src.length, j, c;
    while (i < n) {
      c = src[i];
      if (/\s/.test(c) || c === '~') { i++; continue; }
      if (c === '\u2212') { t.push({ k: 'op', v: '-' }); i++; continue; }
      if (c === '\\') {
        j = i + 1;
        if (j < n && /[A-Za-z]/.test(src[j])) {
          while (j < n && /[A-Za-z]/.test(src[j])) j++;
          var name = src.slice(i + 1, j);
          i = j;
          if (name === 'left' || name === 'right' || name === 'displaystyle' || name === 'limits') {
            if (src[i] === '.') i++;
            continue;
          }
          if (name === 'cdot' || name === 'times') { t.push({ k: 'op', v: '*' }); continue; }
          if (name === 'div') { t.push({ k: 'op', v: '/' }); continue; }
          t.push({ k: 'cmd', v: name });
          continue;
        }
        var d = src[j] || '';
        if (d === '\\') { t.push({ k: 'op', v: ';' }); i = j + 1; continue; }   // Zeilenumbruch trennt Funktionen
        if (d === '|') { t.push({ k: 'op', v: '|' }); i = j + 1; continue; }
        i = j + 1;                                                              // \, \; \! \  \{ \} ...
        continue;
      }
      if (/[0-9.]/.test(c)) {
        j = i;
        while (j < n && /[0-9.]/.test(src[j])) j++;
        var s = src.slice(i, j);
        if (s !== '.') t.push({ k: 'num', v: parseFloat(s) });
        i = j;
        continue;
      }
      if (/[A-Za-z]/.test(c)) { t.push({ k: 'id', v: c }); i++; continue; }
      if ('+-*/^_(){}[]|=,;'.indexOf(c) >= 0) { t.push({ k: 'op', v: c }); i++; continue; }
      throw new Error('Unbekanntes Zeichen: ' + c);
    }
    return t;
  }

  // ---------------------------------------------------------------- Parser
  var FUNCS = ['sin', 'cos', 'tan', 'cot', 'sec', 'csc', 'arcsin', 'arccos', 'arctan',
    'sinh', 'cosh', 'tanh', 'exp', 'ln', 'log', 'lg'];
  var INVERSE = { sin: 'arcsin', cos: 'arccos', tan: 'arctan' };

  function Parser(toks) { this.t = toks; this.i = 0; this.abs = 0; }
  Parser.prototype.peek = function () { return this.t[this.i]; };
  Parser.prototype.isOp = function (v) { var k = this.t[this.i]; return !!k && k.k === 'op' && k.v === v; };
  Parser.prototype.expect = function (v) {
    if (!this.isOp(v)) throw new Error("'" + v + "' erwartet");
    this.i++;
  };
  Parser.prototype.parseExpr = function () {
    var a = this.parseTerm();
    while (this.isOp('+') || this.isOp('-')) {
      var op = this.t[this.i++].v;
      a = { t: 'bin', op: op, a: a, b: this.parseTerm() };
    }
    return a;
  };
  Parser.prototype.startsFactor = function () {
    var k = this.peek();
    if (!k) return false;
    if (k.k === 'num' || k.k === 'id' || k.k === 'cmd') return true;
    if (k.k === 'op') return k.v === '(' || k.v === '{' || k.v === '[' || (k.v === '|' && this.abs === 0);
    return false;
  };
  Parser.prototype.parseTerm = function () {
    var a = this.parseUnary();
    for (;;) {
      if (this.isOp('*') || this.isOp('/')) {
        var op = this.t[this.i++].v;
        a = { t: 'bin', op: op, a: a, b: this.parseUnary() };
      } else if (this.startsFactor()) {
        a = { t: 'bin', op: '*', a: a, b: this.parsePower() };   // implizite Multiplikation: 2x, (x+1)(x-1)
      } else break;
    }
    return a;
  };
  Parser.prototype.parseUnary = function () {
    if (this.isOp('-')) { this.i++; return { t: 'neg', a: this.parseUnary() }; }
    if (this.isOp('+')) { this.i++; return this.parseUnary(); }
    return this.parsePower();
  };
  Parser.prototype.parsePower = function () {
    var base = this.parsePrimary();
    if (this.isOp('^')) { this.i++; return { t: 'pow', a: base, b: this.parseExponent() }; }
    return base;
  };
  Parser.prototype.parseExponent = function () {
    if (this.isOp('{')) return this.parseGroup();
    if (this.isOp('-')) { this.i++; return { t: 'neg', a: this.parseExponent() }; }
    if (this.isOp('+')) { this.i++; return this.parseExponent(); }
    var k = this.peek();
    if (k && k.k === 'num') { this.i++; return { t: 'num', v: k.v }; }
    return this.parsePrimary();
  };
  Parser.prototype.parseGroup = function () {
    this.expect('{');
    var e = this.parseExpr();
    this.expect('}');
    return e;
  };
  Parser.prototype.readNameGroup = function () {
    this.expect('{');
    var s = '';
    while (this.peek() && this.peek().k === 'id') s += this.t[this.i++].v;
    this.expect('}');
    return s;
  };
  Parser.prototype.parseFnArg = function () {
    if (this.isOp('(')) { this.i++; var e = this.parseExpr(); this.expect(')'); return e; }
    if (this.isOp('{')) return this.parseGroup();
    var first = this.peek();
    if (first && first.k === 'num') {          // \sin 2x  ->  sin(2x)
      var a = this.parsePower();
      for (;;) {
        var k = this.peek();
        if (k && (k.k === 'id' || (k.k === 'cmd' && k.v === 'pi') || (k.k === 'op' && k.v === '('))) {
          a = { t: 'bin', op: '*', a: a, b: this.parsePower() };
        } else break;
      }
      return a;
    }
    return this.parsePower();                  // \sin x, \sin x^2
  };
  Parser.prototype.parseFunction = function (name) {
    var pexp = null, base = null;
    if (this.isOp('^')) { this.i++; pexp = this.parseExponent(); }
    if (this.isOp('_')) { this.i++; base = this.isOp('{') ? this.parseGroup() : this.parseExponent(); }
    var arg = this.parseFnArg();
    var node;
    if (name === 'log' && base) node = { t: 'logb', a: arg, b: base };
    else node = { t: 'fn', n: name === 'lg' ? 'log' : name, a: arg };
    if (pexp) {
      if (pexp.t === 'neg' && pexp.a.t === 'num' && pexp.a.v === 1 && INVERSE[name]) node = { t: 'fn', n: INVERSE[name], a: arg };
      else node = { t: 'pow', a: node, b: pexp };
    }
    return node;
  };
  Parser.prototype.parsePrimary = function () {
    var k = this.t[this.i++];
    if (!k) throw new Error('Ausdruck ist unvollstaendig');
    if (k.k === 'num') return { t: 'num', v: k.v };
    if (k.k === 'id') {
      if (k.v === 'x') return { t: 'var' };
      if (k.v === 'e') return { t: 'const', n: 'e' };
      throw new Error('Unbekannte Variable "' + k.v + '" (nur x ist erlaubt)');
    }
    if (k.k === 'op') {
      if (k.v === '(' || k.v === '[') {
        var e = this.parseExpr();
        this.expect(k.v === '(' ? ')' : ']');
        return e;
      }
      if (k.v === '{') { var g = this.parseExpr(); this.expect('}'); return g; }
      if (k.v === '|') {
        this.abs++;
        var inner = this.parseExpr();
        this.expect('|');
        this.abs--;
        return { t: 'abs', a: inner };
      }
      throw new Error("Unerwartetes Zeichen '" + k.v + "'");
    }
    var c = k.v;
    if (c === 'pi') return { t: 'const', n: 'pi' };
    if (c === 'frac' || c === 'dfrac' || c === 'tfrac') {
      var num = this.parseGroup(), den = this.parseGroup();
      return { t: 'bin', op: '/', a: num, b: den };
    }
    if (c === 'sqrt') {
      var n = null;
      if (this.isOp('[')) { this.i++; n = this.parseExpr(); this.expect(']'); }
      var arg = this.isOp('{') ? this.parseGroup() : this.parsePrimary();
      return { t: 'root', a: arg, n: n };
    }
    if (c === 'mathrm' || c === 'operatorname' || c === 'text' || c === 'mathit') {
      var nm = this.readNameGroup();
      if (nm === 'e') return { t: 'const', n: 'e' };
      if (FUNCS.indexOf(nm) >= 0) return this.parseFunction(nm);
      throw new Error('Unbekannter Name ' + nm);
    }
    if (FUNCS.indexOf(c) >= 0) return this.parseFunction(c);
    throw new Error('Unbekannter Befehl \\' + c);
  };

  function lhsName(toks) {
    var s = toks.map(function (t) { return t.v; }).join('');
    if (/^[A-Za-z]$/.test(s) && s !== 'x') return s;
    if (/^[A-Za-z]\(x\)$/.test(s)) return s;
    throw new Error('Nur Funktionen der Form y = f(x) werden gezeichnet');
  }

  function parseOne(toks) {
    var depth = 0, eq = -1, i;
    for (i = 0; i < toks.length; i++) {
      var tk = toks[i];
      if (tk.k === 'op') {
        if (tk.v === '(' || tk.v === '{' || tk.v === '[') depth++;
        else if (tk.v === ')' || tk.v === '}' || tk.v === ']') depth--;
        else if (tk.v === '=' && depth === 0) { eq = i; break; }
      }
    }
    var name = null, body = toks;
    if (eq >= 0) { name = lhsName(toks.slice(0, eq)); body = toks.slice(eq + 1); }
    var p = new Parser(body);
    var ast = p.parseExpr();
    if (p.i < body.length) throw new Error('Unerwartetes Zeichen im Ausdruck');
    return { name: name, ast: ast };
  }

  function parseFunctions(src) {
    var toks = tokenize(src), parts = [], cur = [], depth = 0;
    toks.forEach(function (tk) {
      if (tk.k === 'op') {
        if (tk.v === '(' || tk.v === '{' || tk.v === '[') depth++;
        else if (tk.v === ')' || tk.v === '}' || tk.v === ']') depth--;
        if ((tk.v === ';' || tk.v === ',') && depth === 0) { parts.push(cur); cur = []; return; }
      }
      cur.push(tk);
    });
    parts.push(cur);
    parts = parts.filter(function (p) { return p.length; });
    if (!parts.length) throw new Error('Keine Funktion angegeben');
    return parts.map(parseOne);
  }

  // ---------------------------------------------------------------- Auswertung
  var FN = {
    sin: Math.sin, cos: Math.cos, tan: Math.tan,
    cot: function (v) { return 1 / Math.tan(v); }, sec: function (v) { return 1 / Math.cos(v); }, csc: function (v) { return 1 / Math.sin(v); },
    arcsin: Math.asin, arccos: Math.acos, arctan: Math.atan,
    sinh: Math.sinh, cosh: Math.cosh, tanh: Math.tanh,
    exp: Math.exp, ln: Math.log, log: Math.log10
  };

  function compile(n) {
    var a, b, f;
    switch (n.t) {
      case 'num': var v = n.v; return function () { return v; };
      case 'var': return function (x) { return x; };
      case 'const': var c = n.n === 'pi' ? Math.PI : Math.E; return function () { return c; };
      case 'neg': f = compile(n.a); return function (x) { return -f(x); };
      case 'abs': f = compile(n.a); return function (x) { return Math.abs(f(x)); };
      case 'fn': f = compile(n.a); var g = FN[n.n]; return function (x) { return g(f(x)); };
      case 'logb': a = compile(n.a); b = compile(n.b); return function (x) { return Math.log(a(x)) / Math.log(b(x)); };
      case 'pow': a = compile(n.a); b = compile(n.b); return function (x) { return Math.pow(a(x), b(x)); };
      case 'root':
        a = compile(n.a);
        if (!n.n) return function (x) { return Math.sqrt(a(x)); };
        b = compile(n.n);
        return function (x) {
          var r = a(x), k = b(x);
          if (r < 0 && Number.isInteger(k) && k % 2 !== 0) return -Math.pow(-r, 1 / k);
          return Math.pow(r, 1 / k);
        };
      case 'bin':
        a = compile(n.a); b = compile(n.b);
        switch (n.op) {
          case '+': return function (x) { return a(x) + b(x); };
          case '-': return function (x) { return a(x) - b(x); };
          case '*': return function (x) { return a(x) * b(x); };
          default: return function (x) { return a(x) / b(x); };
        }
    }
    throw new Error('Interner Fehler: ' + n.t);
  }

  // ---------------------------------------------------------------- Bereiche, Ticks
  function niceStep(span) {
    var raw = span / 8, p = Math.pow(10, Math.floor(Math.log10(raw))), m = raw / p;
    return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * p;
  }
  function clean(v) { return parseFloat(v.toPrecision(10)); }

  function autoY(fs, xmin, xmax) {
    var ys = [], N = 800, i, k;
    for (k = 0; k < fs.length; k++) {
      for (i = 0; i <= N; i++) {
        var y = fs[k].f(xmin + (xmax - xmin) * i / N);
        if (isFinite(y) && Math.abs(y) < 1e6) ys.push(y);
      }
    }
    if (!ys.length) return [-5, 5];
    ys.sort(function (p, q) { return p - q; });
    var lo = Math.min(0, ys[Math.floor(0.02 * (ys.length - 1))]);
    var hi = Math.max(0, ys[Math.ceil(0.98 * (ys.length - 1))]);
    if (hi - lo < 1e-6) { lo -= 1; hi += 1; }
    var pad = 0.1 * (hi - lo);
    return [lo < 0 ? lo - pad : lo, hi > 0 ? hi + pad : hi];
  }

  function ranges(fs, props) {
    props = props || {};
    var xmin = isNum(props.xmin) ? props.xmin : -5, xmax = isNum(props.xmax) ? props.xmax : 5;
    if (xmax <= xmin) { xmin = -5; xmax = 5; }
    var ymin = props.ymin, ymax = props.ymax;
    if (!isNum(ymin) || !isNum(ymax)) {
      var auto = autoY(fs, xmin, xmax);
      if (!isNum(ymin)) ymin = auto[0];
      if (!isNum(ymax)) ymax = auto[1];
    }
    if (ymax <= ymin) { var a2 = autoY(fs, xmin, xmax); ymin = a2[0]; ymax = a2[1]; }
    return { xmin: xmin, xmax: xmax, ymin: clean(ymin), ymax: clean(ymax) };
  }
  function isNum(v) { return typeof v === 'number' && isFinite(v); }

  function compileAll(text) {
    return parseFunctions(text).map(function (p) { return { name: p.name, ast: p.ast, f: compile(p.ast) }; });
  }

  // ---------------------------------------------------------------- SVG
  var COLORS = ['#1f4fd8', '#d62828', '#2a9d3f', '#9b5de5'];
  function fmt(v) { return String(clean(v)).replace('-', '\u2212'); }
  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;'); }

  function graphSvg(text, props) {
    var fs = compileAll(text), R = ranges(fs, props);
    var W = 640, H = 480, ml = 44, mr = 24, mt = 24, mb = 36, pw = W - ml - mr, ph = H - mt - mb;
    function px(x) { return ml + (x - R.xmin) / (R.xmax - R.xmin) * pw; }
    function py(y) { return mt + (R.ymax - y) / (R.ymax - R.ymin) * ph; }
    var ax = Math.min(Math.max(0, R.xmin), R.xmax), ay = Math.min(Math.max(0, R.ymin), R.ymax);
    var sx = niceStep(R.xmax - R.xmin), sy = niceStep(R.ymax - R.ymin), out = [], v;
    out.push('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ' + W + ' ' + H + '" font-family="Segoe UI, sans-serif" font-size="15">');
    out.push('<defs><clipPath id="plotclip"><rect x="' + ml + '" y="' + mt + '" width="' + pw + '" height="' + ph + '"/></clipPath></defs>');
    out.push('<rect width="' + W + '" height="' + H + '" fill="#fff"/>');
    // Gitter und Ticks
    for (v = Math.ceil(R.xmin / sx) * sx; v <= R.xmax + 1e-9; v += sx) {
      var X = px(v);
      out.push('<line x1="' + X + '" y1="' + mt + '" x2="' + X + '" y2="' + (mt + ph) + '" stroke="#e3e3e3"/>');
      if (Math.abs(v) > 1e-9) out.push('<text x="' + X + '" y="' + (py(ay) + 18) + '" text-anchor="middle" fill="#444">' + fmt(v) + '</text>');
    }
    for (v = Math.ceil(R.ymin / sy) * sy; v <= R.ymax + 1e-9; v += sy) {
      var Y = py(v);
      out.push('<line x1="' + ml + '" y1="' + Y + '" x2="' + (ml + pw) + '" y2="' + Y + '" stroke="#e3e3e3"/>');
      if (Math.abs(v) > 1e-9) out.push('<text x="' + (px(ax) - 8) + '" y="' + (Y + 5) + '" text-anchor="end" fill="#444">' + fmt(v) + '</text>');
    }
    // Achsen mit Pfeilen
    out.push('<line x1="' + ml + '" y1="' + py(ay) + '" x2="' + (ml + pw) + '" y2="' + py(ay) + '" stroke="#111" stroke-width="1.6"/>');
    out.push('<line x1="' + px(ax) + '" y1="' + (mt + ph) + '" x2="' + px(ax) + '" y2="' + mt + '" stroke="#111" stroke-width="1.6"/>');
    out.push('<polygon points="' + (ml + pw + 6) + ',' + py(ay) + ' ' + (ml + pw - 6) + ',' + (py(ay) - 4) + ' ' + (ml + pw - 6) + ',' + (py(ay) + 4) + '" fill="#111"/>');
    out.push('<polygon points="' + px(ax) + ',' + (mt - 6) + ' ' + (px(ax) - 4) + ',' + (mt + 6) + ' ' + (px(ax) + 4) + ',' + (mt + 6) + '" fill="#111"/>');
    out.push('<text x="' + (ml + pw - 4) + '" y="' + (py(ay) - 8) + '" text-anchor="end" font-style="italic">x</text>');
    out.push('<text x="' + (px(ax) + 10) + '" y="' + (mt + 12) + '" font-style="italic">y</text>');
    // Kurven (bei Polstellen/ausserhalb des Bereichs wird die Linie unterbrochen)
    var span = R.ymax - R.ymin, N = 700;
    fs.forEach(function (fn, idx) {
      var d = '', pen = false, i;
      for (i = 0; i <= N; i++) {
        var x = R.xmin + (R.xmax - R.xmin) * i / N, y = fn.f(x);
        var ok = isFinite(y) && y > R.ymin - span && y < R.ymax + span;
        if (ok) { d += (pen ? 'L' : 'M') + px(x).toFixed(1) + ' ' + py(y).toFixed(1) + ' '; pen = true; } else pen = false;
      }
      out.push('<path d="' + d + '" fill="none" stroke="' + COLORS[idx % COLORS.length] + '" stroke-width="2.8" stroke-linejoin="round" clip-path="url(#plotclip)"/>');
    });
    // Legende bei mehreren Funktionen
    if (fs.length > 1) {
      fs.forEach(function (fn, idx) {
        var y = mt + 18 + idx * 22, nm = fn.name || ('f' + (idx + 1));
        out.push('<line x1="' + (ml + pw - 110) + '" y1="' + (y - 5) + '" x2="' + (ml + pw - 84) + '" y2="' + (y - 5) + '" stroke="' + COLORS[idx % COLORS.length] + '" stroke-width="3"/>');
        out.push('<text x="' + (ml + pw - 78) + '" y="' + y + '" fill="#222">' + esc(nm) + '</text>');
      });
    }
    out.push('</svg>');
    return out.join('');
  }

  // ---------------------------------------------------------------- TikZ / pgfplots
  var PGF_FN = { sin: 'sin', cos: 'cos', tan: 'tan', cot: 'cot', sec: 'sec', csc: 'csc', arcsin: 'asin', arccos: 'acos', arctan: 'atan',
    sinh: 'sinh', cosh: 'cosh', tanh: 'tanh', exp: 'exp', ln: 'ln', log: 'log10' };

  function numStr(v) { var s = String(clean(v)); return s.indexOf('e') >= 0 ? v.toFixed(8).replace(/0+$/, '').replace(/\.$/, '') : s; }

  function pgf(n) {
    function wrap(m) {
      var s = pgf(m);
      return (m.t === 'num' || m.t === 'var' || m.t === 'const' || m.t === 'fn' || m.t === 'abs' || (m.t === 'root')) ? s : '(' + s + ')';
    }
    switch (n.t) {
      case 'num': return numStr(n.v);
      case 'var': return 'x';
      case 'const': return n.n === 'pi' ? 'pi' : 'exp(1)';
      case 'neg': return '-' + wrap(n.a);
      case 'abs': return 'abs(' + pgf(n.a) + ')';
      case 'fn': return PGF_FN[n.n] + '(' + pgf(n.a) + ')';
      case 'logb': return 'ln(' + pgf(n.a) + ')/ln(' + pgf(n.b) + ')';
      case 'root': return n.n ? wrap(n.a) + '^(1/' + wrap(n.n) + ')' : 'sqrt(' + pgf(n.a) + ')';
      case 'pow': return wrap(n.a) + '^' + wrap(n.b);
      case 'bin': return wrap(n.a) + n.op + wrap(n.b);
    }
    throw new Error('Interner Fehler: ' + n.t);
  }

  var TIKZ_COLORS = ['blue', 'red', 'green!60!black', 'violet'];

  function graphToTikz(text, props) {
    var fs = compileAll(text), R = ranges(fs, props), span = R.ymax - R.ymin, lines = [];
    lines.push('% benoetigt: \\usepackage{pgfplots} und \\pgfplotsset{compat=1.18}');
    lines.push('\\begin{tikzpicture}');
    lines.push('\\begin{axis}[');
    lines.push('  axis lines=middle, xlabel={$x$}, ylabel={$y$},');
    lines.push('  xmin=' + numStr(R.xmin) + ', xmax=' + numStr(R.xmax) + ', ymin=' + numStr(R.ymin) + ', ymax=' + numStr(R.ymax) + ',');
    lines.push('  trig format plots=rad, samples=400, domain=' + numStr(R.xmin) + ':' + numStr(R.xmax) + ',');
    lines.push('  unbounded coords=jump, restrict y to domain=' + numStr(R.ymin - span) + ':' + numStr(R.ymax + span) + ',');
    lines.push('  grid=major, grid style={gray!25}, width=10cm, height=7.5cm' + (fs.length > 1 ? ', legend pos=north east' : ''));
    lines.push(']');
    fs.forEach(function (fn, idx) {
      lines.push('\\addplot[' + TIKZ_COLORS[idx % TIKZ_COLORS.length] + ', thick] {' + pgf(fn.ast) + '};');
      if (fs.length > 1) lines.push('\\addlegendentry{$' + (fn.name || ('f_' + (idx + 1))) + '$}');
    });
    lines.push('\\end{axis}');
    lines.push('\\end{tikzpicture}');
    return lines.join('\n');
  }

  // ---------------------------------------------------------------- Einbindung in die Seiten
  var api = { tokenize: tokenize, parseFunctions: parseFunctions, compile: compile, ranges: ranges,
    graphSvg: graphSvg, graphToTikz: graphToTikz };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else {
    root.MathPlot = api;
    root.renderGraph = function (el, text, props) {
      try { el.innerHTML = graphSvg(text, props || {}); }
      catch (e) { el.innerHTML = '<div class="err">Graph-Fehler: ' + esc(e.message) + '</div>'; }
    };
    root.graphTikzResult = function (text, props) {
      try { return { ok: true, text: graphToTikz(text, props || {}) }; }
      catch (e) { return { ok: false, error: e.message }; }
    };
  }
})(typeof window !== 'undefined' ? window : globalThis);
