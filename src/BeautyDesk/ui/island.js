// «Остров»: что сейчас играет. Свёрнут — обложка и название; при наведении раскрывается в плеер.
if (BD.role === 'island') {
  const $ = (id) => document.getElementById(id);
  const island = $('island');
  const playIcon = '<svg viewBox="0 0 16 16" width="18" height="18"><path d="M4 2.5v11l9-5.5z" fill="currentColor"/></svg>';
  const pauseIcon = '<svg viewBox="0 0 16 16" width="18" height="18"><path d="M3.5 2.5h3v11h-3zM9.5 2.5h3v11h-3z" fill="currentColor"/></svg>';

  let media = null;
  let collapseTimer = null;

  const time = (seconds) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`;

  /** Позиция сейчас: плеер сообщает её изредка, между сообщениями досчитываем по часам. */
  function position() {
    if (!media) return 0;
    const elapsed = media.playing ? (Date.now() - media.updatedAt) / 1000 : 0;
    const value = media.position + Math.max(0, elapsed);
    return media.duration > 0 ? Math.min(value, media.duration) : value;
  }

  function render() {
    island.hidden = !media;
    if (!media) return;
    $('media-title').textContent = media.title;
    $('media-artist').textContent = media.artist;
    const cover = $('media-cover');
    cover.hidden = !media.cover;
    if (media.cover && cover.getAttribute('src') !== media.cover) cover.setAttribute('src', media.cover);
    $('media-toggle').innerHTML = media.playing ? pauseIcon : playIcon;
    island.classList.toggle('playing', media.playing);
    island.classList.toggle('no-timeline', !(media.duration > 0));
    renderProgress();
  }

  function renderProgress() {
    if (!media || !(media.duration > 0)) return;
    const now = position();
    $('media-progress-fill').style.width = `${(now / media.duration) * 100}%`;
    $('media-position').textContent = time(now);
    $('media-duration').textContent = time(media.duration);
  }

  function setExpanded(expanded) {
    clearTimeout(collapseTimer);
    if (expanded) {
      // Сначала программа открывает окно целиком, потом плашка плавно растёт.
      BD.send({ type: 'islandExpand', value: true });
      island.classList.add('expanded');
    } else {
      // Сначала плашка плавно сжимается, потом программа обрезает окно до её размера.
      island.classList.remove('expanded');
      collapseTimer = setTimeout(() => BD.send({ type: 'islandExpand', value: false }), 260);
    }
  }

  // Окно обрезано по плашке, поэтому курсор «входит» и «выходит» ровно по её краям.
  document.documentElement.addEventListener('mouseenter', () => setExpanded(true));
  document.documentElement.addEventListener('mouseleave', () => {
    clearTimeout(collapseTimer);
    collapseTimer = setTimeout(() => setExpanded(false), 300);
  });

  $('media-previous').addEventListener('click', () => BD.send({ type: 'media', action: 'previous' }));
  $('media-toggle').addEventListener('click', () => BD.send({ type: 'media', action: 'toggle' }));
  $('media-next').addEventListener('click', () => BD.send({ type: 'media', action: 'next' }));
  $('media-progress').addEventListener('click', (event) => {
    if (!media || !(media.duration > 0)) return;
    const r = $('media-progress').getBoundingClientRect();
    BD.send({ type: 'media', action: 'seek', position: ((event.clientX - r.left) / r.width) * media.duration });
  });

  BD.on('media', (data) => { media = data; render(); });
  setInterval(() => { if (island.classList.contains('expanded') && !BD.state?.paused) renderProgress(); }, 500);
}
