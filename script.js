/* Local-only, dependency-free interactions. */
(() => {
  const tabs = [...document.querySelectorAll('[role="tab"]')];
  function activateTab(tab) {
    tabs.forEach((item) => {
      const selected = item === tab;
      item.setAttribute('aria-selected', String(selected));
      item.tabIndex = selected ? 0 : -1;
      document.getElementById(item.getAttribute('aria-controls')).hidden = !selected;
    });
  }
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => activateTab(tab));
    tab.addEventListener('keydown', (event) => {
      let next;
      if (event.key === 'ArrowRight') next = (index + 1) % tabs.length;
      if (event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = tabs.length - 1;
      if (next !== undefined) {
        event.preventDefault();
        activateTab(tabs[next]);
        tabs[next].focus();
      }
    });
  });

  const dialog = document.querySelector('.image-dialog');
  const dialogImage = document.getElementById('dialog-image');
  document.querySelectorAll('.image-expand').forEach((button) => {
    button.addEventListener('click', () => {
      dialogImage.src = button.dataset.image;
      dialogImage.alt = button.dataset.alt;
      document.getElementById('dialog-caption').textContent = button.dataset.alt;
      dialog.showModal();
    });
  });
  document.querySelector('.dialog-close').addEventListener('click', () => dialog.close());
  dialog.addEventListener('click', (event) => {
    const bounds = dialog.getBoundingClientRect();
    if (event.target === dialog && (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom)) dialog.close();
  });

  const videos = [...document.querySelectorAll('video')];
  videos.forEach((video) => {
    video.muted = true;
    // Start offscreen videos too; native controls remain available if playback is blocked.
    video.play().catch(() => {});
    video.addEventListener('error', () => {
      if (video.parentElement.querySelector('.video-error')) return;
      const message = document.createElement('p');
      message.className = 'video-error source-note';
      message.append('This video could not be played. ');
      const link = document.createElement('a');
      link.href = video.querySelector('source').getAttribute('src');
      link.textContent = 'Open the video file';
      message.append(link);
      video.insertAdjacentElement('afterend', message);
    });
  });

  const navLinks = [...document.querySelectorAll('.chapter-inner > a')];
  const sections = navLinks.map((link) => document.querySelector(link.getAttribute('href')));
  let scrollPending = false;
  function updateSection() {
    scrollPending = false;
    let active = 0;
    sections.forEach((section, index) => {
      if (section.getBoundingClientRect().top <= window.innerHeight * 0.38) active = index;
    });
    navLinks.forEach((link, index) => {
      link.classList.toggle('active', index === active);
      if (index === active) link.setAttribute('aria-current', 'location');
      else link.removeAttribute('aria-current');
    });
  }
  window.addEventListener('scroll', () => {
    if (!scrollPending) { scrollPending = true; requestAnimationFrame(updateSection); }
  }, { passive: true });
  updateSection();

  // A mathematical sculpture illustrates spatial interaction; it is not a generated research result.
  const canvas = document.getElementById('mesh-canvas');
  const ctx = canvas.getContext('2d');
  if (!ctx) return;
  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const motionButton = document.getElementById('motion-toggle');
  let paused = reduceMotion.matches;
  let visible = true;
  let angle = -0.35;
  let tilt = 0.43;
  let width = 0;
  let height = 0;
  let animation = 0;
  let lastTime = 0;
  let pointer = null;
  const rows = 64;
  const cols = 22;
  const vertices = [];
  const faces = [];
  for (let i = 0; i < rows; i++) {
    const u = i / rows * Math.PI * 2;
    const radius = 1.38 + 0.17 * Math.cos(3 * u);
    for (let j = 0; j < cols; j++) {
      const v = j / cols * Math.PI * 2;
      const tube = 0.47 + 0.065 * Math.sin(3 * u);
      const r = radius + tube * Math.cos(v);
      vertices.push([r * Math.cos(u), r * Math.sin(u), tube * Math.sin(v) + 0.4 * Math.sin(3 * u)]);
      faces.push([i * cols + j, ((i + 1) % rows) * cols + j, ((i + 1) % rows) * cols + (j + 1) % cols, i * cols + (j + 1) % cols]);
    }
  }
  function project([x, y, z]) {
    const xx = x * Math.cos(angle) + z * Math.sin(angle);
    const zz = -x * Math.sin(angle) + z * Math.cos(angle);
    const yy = y * Math.cos(tilt) - zz * Math.sin(tilt);
    const depth = y * Math.sin(tilt) + zz * Math.cos(tilt);
    const perspective = 5.8 / (5.8 - depth);
    const scale = Math.min(width, height) * 0.205;
    return { x: width * 0.51 + xx * scale * perspective, y: height * 0.48 + yy * scale * perspective, z: depth };
  }
  function draw() {
    ctx.clearRect(0, 0, width, height);
    const glow = ctx.createRadialGradient(width * .51, height * .49, 10, width * .51, height * .49, width * .44);
    glow.addColorStop(0, '#9850ed18');
    glow.addColorStop(.6, '#7934d608');
    glow.addColorStop(1, '#7934d600');
    ctx.fillStyle = glow;
    ctx.fillRect(0, 0, width, height);
    ctx.save();
    ctx.translate(width * .51, height * .51);
    ctx.rotate(-.24);
    ctx.strokeStyle = '#b988e924';
    ctx.lineWidth = .8;
    ctx.beginPath();
    ctx.ellipse(0, 0, width * .425, height * .34, 0, 0, Math.PI * 2);
    ctx.stroke();
    ctx.setLineDash([2, 10]);
    ctx.strokeStyle = '#b988e931';
    ctx.beginPath();
    ctx.ellipse(0, 0, width * .465, height * .39, 0, 0, Math.PI * 2);
    ctx.stroke();
    ctx.restore();
    const projected = vertices.map(project);
    const ordered = faces.map((face) => ({face, z: face.reduce((sum, i) => sum + projected[i].z, 0) / 4})).sort((a, b) => a.z - b.z);
    for (const {face, z} of ordered) {
      const light = Math.max(0, Math.min(1, (z + 2) / 4));
      const points = face.map((i) => projected[i]);
      ctx.beginPath();
      ctx.moveTo(points[0].x, points[0].y);
      for (let i = 1; i < points.length; i++) ctx.lineTo(points[i].x, points[i].y);
      ctx.closePath();
      ctx.fillStyle = `rgba(${Math.round(32 + light * 65)}, ${Math.round(14 + light * 23)}, ${Math.round(57 + light * 96)}, .84)`;
      ctx.fill();
      ctx.strokeStyle = `rgba(${Math.round(153 + light * 64)}, ${Math.round(83 + light * 82)}, 255, ${.14 + light * .42})`;
      ctx.lineWidth = .65;
      ctx.stroke();
      if (light > .78 && face[0] % 5 === 0) {
        ctx.fillStyle = '#eed8ffb0';
        ctx.beginPath();ctx.arc(points[0].x, points[0].y, .85, 0, Math.PI * 2);ctx.fill();
      }
    }
    for (const [px, py, r] of [[.13,.38,2],[.82,.64,2.2],[.75,.18,1.5],[.22,.77,1.5]]) {
      ctx.fillStyle = '#c59be5';
      ctx.beginPath();ctx.arc(width * px, height * py, r, 0, Math.PI * 2);ctx.fill();
      ctx.strokeStyle = '#a373dc35';
      ctx.beginPath();ctx.arc(width * px, height * py, r + 5, 0, Math.PI * 2);ctx.stroke();
    }
  }
  function tick(time) {
    animation = 0;
    if (paused || !visible || document.hidden) { lastTime = 0; return; }
    if (!pointer && lastTime) angle += Math.min(time - lastTime, 40) * .000095;
    lastTime = time;
    draw();
    animation = requestAnimationFrame(tick);
  }
  function start() {
    if (!animation && !paused && visible && !document.hidden) animation = requestAnimationFrame(tick);
  }
  function syncMotionButton() {
    motionButton.setAttribute('aria-pressed', String(paused));
    motionButton.setAttribute('aria-label', paused ? 'Resume mesh rotation' : 'Pause mesh rotation');
    motionButton.textContent = paused ? 'Resume motion ▷' : 'Pause motion Ⅱ';
  }
  function resize() {
    const bounds = canvas.getBoundingClientRect();
    width = bounds.width; height = bounds.height;
    const pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.round(width * pixelRatio);
    canvas.height = Math.round(height * pixelRatio);
    ctx.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
    draw();
  }
  new ResizeObserver(resize).observe(canvas);
  new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; start(); }).observe(canvas);
  document.addEventListener('visibilitychange', start);
  motionButton.addEventListener('click', () => { paused = !paused; syncMotionButton(); start(); });
  reduceMotion.addEventListener('change', () => { paused = reduceMotion.matches; syncMotionButton(); start(); });
  canvas.addEventListener('pointerdown', (event) => {
    if (event.button !== 0) return;
    pointer = { x: event.clientX, y: event.clientY, id: event.pointerId };
    canvas.setPointerCapture(event.pointerId);
  });
  canvas.addEventListener('pointermove', (event) => {
    if (!pointer || pointer.id !== event.pointerId) return;
    angle += (event.clientX - pointer.x) * .008;
    tilt = Math.max(-1.2, Math.min(1.2, tilt + (event.clientY - pointer.y) * .006));
    pointer = { x: event.clientX, y: event.clientY, id: event.pointerId };
    draw();
  });
  const release = () => { pointer = null; };
  canvas.addEventListener('pointerup', release);
  canvas.addEventListener('pointercancel', release);
  canvas.addEventListener('lostpointercapture', release);
  canvas.addEventListener('keydown', (event) => {
    if (!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key)) return;
    event.preventDefault();
    if (event.key === 'ArrowLeft') angle -= .12;
    if (event.key === 'ArrowRight') angle += .12;
    if (event.key === 'ArrowUp') tilt -= .1;
    if (event.key === 'ArrowDown') tilt += .1;
    tilt = Math.max(-1.2, Math.min(1.2, tilt));
    draw();
  });
  syncMotionButton();
  resize();
  start();
})();
