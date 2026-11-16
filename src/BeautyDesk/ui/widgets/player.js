// Виджет «Плеер»: что сейчас играет, перемотка, управление и эквалайзер — полосы, реагирующие на звук компьютера.
// В большом размере обложка крупная и размыто повторяется фоном, чтобы виджет не выглядел пустым.
BD.widget('player', {
  title: 'widget.player',
  sizes: [[4, 3], [4, 2], [4, 4]],
  create(body) {
    const BARS = 24; // столько полос присылает программа
    const icon = (path, size = 16) => `<svg viewBox="0 0 16 16" width="${size}" height="${size}"><path d="${path}" fill="currentColor"/></svg>`;
    const PLAY = 'M4 2.5v11l9-5.5z', PAUSE = 'M3.5 2.5h3v11h-3zM9.5 2.5h3v11h-3z';
    const NOTE = 'M6 2.5v7.6A2.2 2.2 0 1 0 7.2 12V5.2l5-1.2v4.9A2.2 2.2 0 1 0 13.4 11V1.5z';

    body.innerHTML = `
      <div class="player-backdrop"></div>
      <div class="player-main">
        <div class="player-art">
          <img class="player-cover" alt="">
          <span class="player-noart">${icon(NOTE, 28)}</span>
        </div>
        <div class="player-info">
          <div class="player-title"></div>
          <div class="player-artist"></div>
          <div class="player-progress"><i></i></div>
          <div class="player-times"><span></span><span></span></div>
          <div class="player-controls">
            <button class="plain" data-action="previous">${icon('M3 3h1.6v10H3zM13 3v10L5.5 8z')}</button>
            <button class="plain" data-action="toggle"></button>
            <button class="plain" data-action="next">${icon('M11.4 3H13v10h-1.6zM3 3v10l7.5-5z')}</button>
          </div>
        </div>
      </div>
      <div class="player-bars"></div>`;

    const $ = (selector) => body.querySelector(selector);
    const time = (seconds) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`;
    let media = null;

    // Полосы есть всегда: в тишине они лежат низкой линией, а не появляются только с первым звуком.
    const bars = Array.from({ length: BARS }, () => $('.player-bars').appendChild(document.createElement('i')));

    for (const button of body.querySelectorAll('[data-action]')) {
      button.addEventListener('click', () => BD.send({ type: 'media', action: button.dataset.action }));
    }
    $('.player-progress').addEventListener('click', (event) => {
      if (!media || !(media.duration > 0)) return;
      const box = $('.player-progress').getBoundingClientRect();
      BD.send({ type: 'media', action: 'seek', position: ((event.clientX - box.left) / box.width) * media.duration });
    });

    /** Позиция сейчас: плеер сообщает её изредка, между сообщениями досчитываем по часам. */
    function position() {
      if (!media) return 0;
      const elapsed = media.playing ? Math.max(0, (Date.now() - media.updatedAt) / 1000) : 0;
      return media.duration > 0 ? Math.min(media.position + elapsed, media.duration) : media.position + elapsed;
    }

    function renderProgress() {
      const known = Boolean(media && media.duration > 0);
      body.classList.toggle('no-timeline', !known);
      if (!known) return;
      const now = position();
      $('.player-progress i').style.width = `${(now / media.duration) * 100}%`;
      const [current, total] = $('.player-times').children;
      current.textContent = time(now);
      total.textContent = time(media.duration);
    }

    function render() {
      $('.player-title').textContent = media?.title ?? BD.t('widget.player.empty');
      $('.player-artist').textContent = media?.artist ?? '';
      const cover = $('.player-cover');
      const hasCover = Boolean(media?.cover);
      cover.hidden = !hasCover;
      $('.player-noart').hidden = hasCover;
      if (hasCover && cover.getAttribute('src') !== media.cover) cover.setAttribute('src', media.cover);
      $('.player-backdrop').style.backgroundImage = hasCover ? `url("${media.cover}")` : '';
      $('[data-action="toggle"]').innerHTML = icon(media?.playing ? PAUSE : PLAY, 18);
      body.classList.toggle('empty', !media);
      renderProgress();
    }

    const onMedia = (data) => { media = data; render(); };
    const onAudio = (levels) => {
      levels.forEach((level, i) => { if (bars[i]) bars[i].style.height = `${Math.max(4, level * 100)}%`; });
    };
    BD.on('media', onMedia);
    BD.on('audio', onAudio);
    render();

    let language = BD.state?.language;
    return {
      // Вызывается раз в секунду: двигает полосу перемотки; при смене языка обновляет подписи.
      update() {
        if (BD.state?.language !== language) { language = BD.state?.language; render(); }
        else renderProgress();
      },
      destroy() {
        BD.off('media', onMedia);
        BD.off('audio', onAudio);
      },
    };
  },
});
