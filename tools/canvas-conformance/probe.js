(() => {
  const R = {};
  const mk = (w, h) => { const c = document.createElement('canvas'); c.width = w || 60; c.height = h || 40; return [c, c.getContext('2d')]; };
  // count of non-transparent pixels, and the mean colour of those, so a wrong
  // colour is caught as well as a wrong shape
  const stat = (c, x) => {
    try {
      const d = x.getImageData(0, 0, c.width, c.height).data;
      let n = 0, r = 0, g = 0, b = 0;
      for (let i = 0; i < d.length; i += 4) if (d[i + 3] > 8) { n++; r += d[i]; g += d[i + 1]; b += d[i + 2]; }
      return n ? [n, Math.round(r / n), Math.round(g / n), Math.round(b / n)] : [0, 0, 0, 0];
    } catch (e) { return 'ERR'; }
  };
  const t = (name, fn, w, h) => { const [c, x] = mk(w, h); try { fn(x, c); R[name] = stat(c, x); } catch (e) { R[name] = 'THREW'; } };

  t('arcFill', x => { x.fillStyle = '#f00'; x.beginPath(); x.arc(30, 20, 10, 0, Math.PI * 2); x.fill(); });
  t('arcStroke', x => { x.strokeStyle = '#0f0'; x.lineWidth = 2; x.beginPath(); x.arc(30, 20, 10, 0, Math.PI * 2); x.stroke(); });
  t('halfArc', x => { x.fillStyle = '#00f'; x.beginPath(); x.arc(30, 20, 10, 0, Math.PI); x.closePath(); x.fill(); });
  t('bezier', x => { x.strokeStyle = '#fff'; x.lineWidth = 2; x.beginPath(); x.moveTo(5, 35); x.bezierCurveTo(20, 0, 40, 40, 55, 5); x.stroke(); });
  t('quad', x => { x.strokeStyle = '#fff'; x.lineWidth = 2; x.beginPath(); x.moveTo(5, 35); x.quadraticCurveTo(30, 0, 55, 35); x.stroke(); });
  t('rectPathFill', x => { x.fillStyle = '#0ff'; x.beginPath(); x.rect(10, 10, 20, 15); x.fill(); });
  t('rectPathNoPaint', x => { x.fillStyle = '#0ff'; x.beginPath(); x.rect(10, 10, 20, 15); });
  t('evenOdd', x => { x.fillStyle = '#ff0'; x.beginPath(); x.rect(5, 5, 40, 30); x.rect(15, 12, 20, 16); x.fill('evenodd'); });
  t('nonZero', x => { x.fillStyle = '#ff0'; x.beginPath(); x.rect(5, 5, 40, 30); x.rect(15, 12, 20, 16); x.fill('nonzero'); });
  t('clipRect', x => { x.beginPath(); x.rect(10, 10, 10, 10); x.clip(); x.fillStyle = '#f0f'; x.fillRect(0, 0, 60, 40); });
  t('clipRestore', x => { x.save(); x.beginPath(); x.rect(10, 10, 10, 10); x.clip(); x.restore(); x.fillStyle = '#f0f'; x.fillRect(0, 0, 60, 40); });
  t('translateFill', x => { x.translate(10, 10); x.fillStyle = '#f00'; x.fillRect(0, 0, 10, 10); });
  t('scaleFill', x => { x.scale(2, 3); x.fillStyle = '#f00'; x.fillRect(0, 0, 10, 5); });
  t('rotateFill', x => { x.translate(30, 20); x.rotate(Math.PI / 4); x.fillStyle = '#f00'; x.fillRect(-7, -7, 14, 14); });
  t('saveRestoreTransform', x => { x.save(); x.translate(20, 20); x.restore(); x.fillStyle = '#f00'; x.fillRect(0, 0, 10, 10); });
  t('lineWidthScaled', x => { x.scale(3, 3); x.strokeStyle = '#0f0'; x.lineWidth = 2; x.beginPath(); x.moveTo(0, 6); x.lineTo(20, 6); x.stroke(); });
  t('dashed', x => { x.strokeStyle = '#fff'; x.lineWidth = 4; x.setLineDash([6, 6]); x.beginPath(); x.moveTo(0, 20); x.lineTo(60, 20); x.stroke(); });
  t('dashedOffset', x => { x.strokeStyle = '#fff'; x.lineWidth = 4; x.setLineDash([6, 6]); x.lineDashOffset = 6; x.beginPath(); x.moveTo(0, 20); x.lineTo(60, 20); x.stroke(); });
  t('roundCap', x => { x.strokeStyle = '#fff'; x.lineWidth = 10; x.lineCap = 'round'; x.beginPath(); x.moveTo(15, 20); x.lineTo(45, 20); x.stroke(); });
  t('buttCap', x => { x.strokeStyle = '#fff'; x.lineWidth = 10; x.lineCap = 'butt'; x.beginPath(); x.moveTo(15, 20); x.lineTo(45, 20); x.stroke(); });
  t('radialGradient', x => { const g = x.createRadialGradient(30, 20, 0, 30, 20, 20); g.addColorStop(0, '#fff'); g.addColorStop(1, '#000'); x.fillStyle = g; x.fillRect(0, 0, 60, 40); });
  t('conicGradient', x => { const g = x.createConicGradient(0, 30, 20); g.addColorStop(0, '#f00'); g.addColorStop(1, '#00f'); x.fillStyle = g; x.fillRect(0, 0, 60, 40); });
  t('gradientStroke', x => { const g = x.createLinearGradient(0, 0, 60, 0); g.addColorStop(0, '#f00'); g.addColorStop(1, '#0f0'); x.strokeStyle = g; x.lineWidth = 6; x.beginPath(); x.moveTo(0, 20); x.lineTo(60, 20); x.stroke(); });
  t('globalAlpha', x => { x.globalAlpha = 0.5; x.fillStyle = '#f00'; x.fillRect(0, 0, 20, 20); });
  t('clearRect', x => { x.fillStyle = '#f00'; x.fillRect(0, 0, 60, 40); x.clearRect(10, 10, 20, 20); });
  t('strokeRect', x => { x.strokeStyle = '#0f0'; x.lineWidth = 2; x.strokeRect(10, 10, 30, 20); });
  t('ellipse', x => { x.fillStyle = '#f0f'; x.beginPath(); x.ellipse(30, 20, 20, 10, 0, 0, Math.PI * 2); x.fill(); });
  t('roundRect', x => { x.fillStyle = '#0f0'; x.beginPath(); x.roundRect(10, 8, 40, 24, 6); x.fill(); });
  t('arcTo', x => { x.strokeStyle = '#fff'; x.lineWidth = 2; x.beginPath(); x.moveTo(5, 35); x.arcTo(5, 5, 55, 5, 12); x.lineTo(55, 5); x.stroke(); });
  t('destinationOut', x => { x.fillStyle = '#f00'; x.fillRect(0, 0, 60, 40); x.globalCompositeOperation = 'destination-out'; x.fillRect(10, 10, 20, 20); });
  t('lighter', x => { x.fillStyle = '#404040'; x.fillRect(0, 0, 60, 40); x.globalCompositeOperation = 'lighter'; x.fillRect(0, 0, 60, 40); });
  t('drawImageScaled', (x) => {
    const [s, sx2] = mk(10, 10); sx2.fillStyle = '#f00'; sx2.fillRect(0, 0, 10, 10);
    x.drawImage(s, 0, 0, 10, 10, 10, 10, 30, 20);
  });
  t('patternFill', x => {
    const [s, sx2] = mk(4, 4); sx2.fillStyle = '#0f0'; sx2.fillRect(0, 0, 2, 2); sx2.fillStyle = '#00f'; sx2.fillRect(2, 2, 2, 2);
    const p = x.createPattern(s, 'repeat'); x.fillStyle = p; x.fillRect(0, 0, 60, 40);
  });
  t('rgbaColor', x => { x.fillStyle = 'rgba(255, 0, 0, 0.5)'; x.fillRect(0, 0, 20, 20); });
  t('hslColor', x => { x.fillStyle = 'hsl(120, 100%, 50%)'; x.fillRect(0, 0, 20, 20); });
  t('namedColor', x => { x.fillStyle = 'rebeccapurple'; x.fillRect(0, 0, 20, 20); });
  t('shortHex', x => { x.fillStyle = '#0f08'; x.fillRect(0, 0, 20, 20); });

  const [pc, px2] = mk();
  px2.beginPath(); px2.rect(10, 10, 20, 20);
  R.isPointInPathIn = px2.isPointInPath(15, 15);
  R.isPointInPathOut = px2.isPointInPath(5, 5);
  px2.beginPath(); px2.moveTo(0, 20); px2.lineTo(60, 20); px2.lineWidth = 8;
  R.isPointInStrokeIn = px2.isPointInStroke(30, 21);
  R.isPointInStrokeOut = px2.isPointInStroke(30, 2);
  R.hasCanvasGradient = typeof CanvasGradient !== 'undefined';
  R.getTransform = (() => { const [, x] = mk(); x.translate(3, 4); const m = x.getTransform(); return [m.a, m.d, m.e, m.f]; })();
  return JSON.stringify(R);
})()
