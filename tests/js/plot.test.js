const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const P = require(path.join(__dirname, '..', '..', 'windows', 'MathNotes.App', 'Assets', 'preview', 'plot.js'));

const f = (src) => P.compile(P.parseFunctions(src)[0].ast);
const near = (a, b, eps = 1e-9) => assert.ok(Math.abs(a - b) <= eps, `${a} != ${b}`);

test('Grundrechenarten und Potenzen', () => {
  near(f('x^{2}-3x+1')(2), -1);
  near(f('x^2')(-3), 9);
  near(f('-x^2')(3), -9);                 // Minus bindet schwaecher als Potenz
  near(f('2^{x}')(3), 8);
  near(f('x^{10}')(2), 1024);
  near(f('x^3-2x')(2), 4);
  near(f('2x+1')(4), 9);
  near(f('3\\cdot x')(2), 6);
  near(f('x \\times x')(5), 25);
  near(f('\\frac{x^2-1}{x+1}')(3), 2);
  near(f('(x+1)(x-1)')(4), 15);
  near(f('x(x+1)')(2), 6);
});

test('Funktionen und Konstanten', () => {
  near(f('\\sin x')(Math.PI / 2), 1);
  near(f('\\sin(x)')(Math.PI / 6), 0.5);
  near(f('\\cos 2x')(0), 1);
  near(f('\\sin^2 x + \\cos^2 x')(1.234), 1);
  near(f('\\sin^{2}(x)')(Math.PI / 2), 1);
  near(f('\\sin^{-1} x')(1), Math.PI / 2);
  near(f('2\\sin(x)\\cos(x)')(0.5), Math.sin(1));
  near(f('e^{-x^2}')(0), 1);
  near(f('e^{x}')(1), Math.E);
  near(f('\\exp(x)')(2), Math.exp(2));
  near(f('\\ln x')(Math.E), 1);
  near(f('\\log x')(100), 2);
  near(f('\\log_{2} x')(8), 3);
  near(f('\\sqrt{x+1}')(8), 3);
  near(f('\\sqrt[3]{x}')(-8), -2);
  near(f('|x|')(-4), 4);
  near(f('|x-1|+1')(-1), 3);
  near(f('\\pi x')(2), 2 * Math.PI);
  near(f('\\left(x+1\\right)^2')(2), 9);
  near(f('\\tanh x')(0), 0);
});

test('Funktionsnamen und mehrere Funktionen', () => {
  const fs = P.parseFunctions('f(x)=x^2, g(x)=\\sin x; y=2x');
  assert.deepStrictEqual(fs.map(x => x.name), ['f(x)', 'g(x)', 'y']);
  near(P.compile(fs[2].ast)(3), 6);
  assert.strictEqual(P.parseFunctions('x^2').length, 1);
  assert.strictEqual(P.parseFunctions('f(x)=\\frac{1}{x}')[0].name, 'f(x)');
  // Komma innerhalb von Klammern trennt nicht
  assert.strictEqual(P.parseFunctions('f(x)=\\frac{x}{2}').length, 1);
});

test('Fehler werden verstaendlich gemeldet', () => {
  assert.throws(() => P.parseFunctions('x^2 +'), /unvollstaendig/);
  assert.throws(() => P.parseFunctions('a x'), /Unbekannte Variable/);
  assert.throws(() => P.parseFunctions('\\foo x'), /Unbekannter Befehl/);
  assert.throws(() => P.parseFunctions('x^2+y^2=1'), /y = f\(x\)/);
  assert.throws(() => P.parseFunctions('(x+1'), /erwartet/);
  assert.throws(() => P.parseFunctions(''), /Keine Funktion/);
  assert.throws(() => P.parseFunctions('x # 2'), /Unbekanntes Zeichen/);
});

test('Bereiche: automatisch und vorgegeben', () => {
  const fs = P.parseFunctions('x^2').map(p => ({ f: P.compile(p.ast) }));
  const r = P.ranges(fs, {});
  assert.strictEqual(r.xmin, -5); assert.strictEqual(r.xmax, 5);
  assert.ok(r.ymin === 0 && r.ymax > 24 && r.ymax < 32, JSON.stringify(r));   // x^2 auf [-5,5]: 0 .. 25 plus Rand
  const r2 = P.ranges(fs, { xmin: -2, xmax: 2, ymin: -1, ymax: 5 });
  assert.deepStrictEqual(r2, { xmin: -2, xmax: 2, ymin: -1, ymax: 5 });
  const r3 = P.ranges(fs, { xmin: 3, xmax: 1 });                              // ungueltig -> Standard
  assert.strictEqual(r3.xmin, -5);
  const pole = P.parseFunctions('\\frac{1}{x}').map(p => ({ f: P.compile(p.ast) }));
  const rp = P.ranges(pole, {});
  assert.ok(rp.ymax < 1e4 && rp.ymin > -1e4, JSON.stringify(rp));            // Polstelle sprengt den Bereich nicht
});

test('SVG enthaelt Kurve, Achsen und bei mehreren Funktionen eine Legende', () => {
  const svg = P.graphSvg('f(x)=x^2', {});
  assert.ok(svg.startsWith('<svg') && svg.endsWith('</svg>'));
  assert.ok(/<path d="M/.test(svg));
  assert.ok(!/NaN|Infinity/.test(svg));
  const two = P.graphSvg('f(x)=x^2, g(x)=x', {});
  assert.ok(two.includes('f(x)') && two.includes('g(x)'));
  const tan = P.graphSvg('\\tan x', {});                                       // Polstellen unterbrechen die Linie
  assert.ok((tan.match(/M[0-9]/g) || []).length > 3);
  assert.ok(!/NaN|Infinity/.test(tan));
});

test('TikZ-Ausgabe: pgfplots-Ausdruecke', () => {
  const t = (s) => P.graphToTikz(s, { xmin: -3, xmax: 3, ymin: -2, ymax: 2 });
  assert.ok(t('x^2').includes('\\addplot[blue, thick] {x^2};'));
  assert.ok(t('\\frac{1}{x}').includes('{1/x};'));
  assert.ok(t('\\sin x').includes('sin(x)'));
  assert.ok(t('e^{-x}').includes('exp(1)^(-x)'));
  assert.ok(t('\\sqrt{x}').includes('sqrt(x)'));
  assert.ok(t('\\ln x').includes('ln(x)'));
  assert.ok(t('-x^2').includes('-(x^2)'));
  assert.ok(t('(x+1)(x-1)').includes('(x+1)*(x-1)'));
  const head = t('x');
  assert.ok(head.includes('xmin=-3, xmax=3, ymin=-2, ymax=2') && head.includes('trig format plots=rad'));
  assert.ok(t('f(x)=x, g(x)=x^2').includes('\\addlegendentry'));
});
