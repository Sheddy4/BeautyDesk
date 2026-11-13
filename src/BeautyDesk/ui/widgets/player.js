// Виджет «Плеер»: что сейчас играет, управление и эквалайзер — полосы, реагирующие на звук компьютера.
BD.widget('player', {
  title: 'widget.player',
  sizes: [[4, 3], [4, 2]],
  create(body) {
    const icon = (path) => `<svg viewBox="0 0 16 16" width="16" height="16"><path d="${path}" fill="currentColor"/></svg>`;
    const PLAY = 'M4 2.5v11l9-5.5z', PAUSE = 'M3.5 2.5h3v11h-3zM9.5 2.5h3v11h-3z';

    body.innerHTML = `
      <div class="player-top">
        <img class="player-cover" alt="">
        <div class="player-text">
          <div class="player-title"></div>
          <div class="player-artist"></div>
        </div>
      </div>
      <div class="player-controls">
        <button class="plain" data-action="previous">${icon('M3 3h1.6v10H3zM13 3v10L5.5 8z')}</button>
        <button class="plain" data-action="toggle"></button>
        <button class="plain" data-action="next">${icon('M11.4 3H13v10h-1.6zM3 3v10l7.5-5z')}</button>
      </div>
      <div class="player-bars"></div>`;

    const $ = (selector) => body.querySelector(selector);
    const bars = [];
    let media = null;

    for (const button of body.querySelectorAll('[data-action]')) {
      button.addEventListener('click', () => BD.send({ type: 'media', action: button.dataset.action }));
    }

    function render() {
      $('.player-title').textContent = media?.title ?? BD.t('widget.player.empty');
      $('.player-artist').textContent = media?.artist ?? '';
      const cover = $('.player-cover');
      cover.hidden = !media?.cover;
      if (media?.cover && cover.getAttribute('src') !== media.cover) cover.setAttribute('src', media.cover);
      $('[data-action="toggle"]').innerHTML = icon(media?.playing ? PAUSE : PLAY);
      body.classList.toggle('empty', !media);
    }

    const onMedia = (data) => { media = data; render(); };
    const onAudio = (levels) => {
      const container = $('.player-bars');
      // Полос столько, сколько прислала программа; создаются при первом сообщении.
      while (bars.length < levels.length) {
        const bar = document.createElement('i');
        container.appendChild(bar);
        bars.push(bar);
      }
      levels.forEach((level, i) => { bars[i].style.height = `${Math.max(4, level * 100)}%`; });
    };
    BD.on('media', onMedia);
    BD.on('audio', onAudio);
    render();

    return {
      update: render, // смена языка меняет подпись «ничего не играет»
      destroy() {
        BD.off('media', onMedia);
        BD.off('audio', onAudio);
      },
    };
  },
});
