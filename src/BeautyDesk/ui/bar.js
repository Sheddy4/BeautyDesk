// Верхняя панель: системное меню, активное приложение, трей, раскладка, звук, сеть, батарея, часы.
if (BD.role === 'bar') {
  const $ = (id) => document.getElementById(id);
  const svg = (body) => `<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;

  const icons = {
    volume(level, muted) {
      const speaker = '<path d="M2 6h2.5L8 3v10L4.5 10H2z" fill="currentColor" stroke="none"/>';
      if (level === null || muted || level === 0) return svg(speaker + '<path d="M10.5 6l4 4M14.5 6l-4 4"/>');
      return svg(speaker + '<path d="M10 6.2a2.6 2.6 0 0 1 0 3.6"/>' + (level > 50 ? '<path d="M11.6 4.4a5 5 0 0 1 0 7.2"/>' : ''));
    },
    network: {
      wifi: svg('<path d="M1.5 6.2a9.5 9.5 0 0 1 13 0"/><path d="M3.8 8.6a6.2 6.2 0 0 1 8.4 0"/><path d="M6 10.9a3 3 0 0 1 4 0"/><circle cx="8" cy="13" r="0.9" fill="currentColor" stroke="none"/>'),
      ethernet: svg('<rect x="2.5" y="3" width="11" height="7.5" rx="1"/><path d="M8 10.5v2.5M5 13.5h6"/>'),
      none: svg('<circle cx="8" cy="8" r="5.5"/><path d="M4.2 11.8l7.6-7.6"/>'),
    },
  };

  let sys = null;

  function renderSys() {
    if (!sys) return;
    $('active-app').textContent = sys.activeApp || BD.t('bar.desktop');

    $('layout').textContent = sys.layout;
    $('layout').hidden = !sys.layout;

    $('volume').innerHTML = icons.volume(sys.volume, sys.muted);
    $('volume').title = sys.volume === null ? BD.t('bar.noSound') : `${sys.volume}%`;

    $('network').innerHTML = icons.network[sys.network] ?? icons.network.none;
    $('network').classList.toggle('muted', sys.network === 'none');

    const battery = $('battery');
    battery.hidden = sys.battery === null;
    if (sys.battery !== null) {
      battery.innerHTML = `<span class="battery-shell"><span class="battery-level" style="width:${sys.battery}%"></span></span><span>${sys.battery}%</span>`;
      battery.classList.toggle('charging', sys.charging);
      battery.classList.toggle('low', sys.battery <= 20 && !sys.charging);
    }
  }

  function renderClock() {
    const now = new Date();
    const language = BD.state?.language ?? 'en';
    const date = new Intl.DateTimeFormat(language, { weekday: 'short', day: 'numeric', month: 'short' }).format(now);
    const time = new Intl.DateTimeFormat(language, { hour: '2-digit', minute: '2-digit' }).format(now);
    $('clock').textContent = `${date}  ${time}`;
  }

  // ---------- трей ----------

  const trayElements = new Map(); // id иконки -> её элемент
  const TRAY_SLOT = 26;           // ширина одной иконки вместе с промежутком, как в base.css
  let trayItems = [];
  let trayHidden = [];            // id иконок, которым не хватило места

  function renderTray(items) {
    trayItems = items;
    const tray = $('tray');
    const seen = new Set();
    for (const item of items) {
      seen.add(item.id);
      let img = trayElements.get(item.id);
      // Элемент живёт, пока жива иконка: так наведение и нажатие не сбиваются при обновлении списка.
      if (!img) trayElements.set(item.id, img = BD.trayIcon(item));
      else BD.updateTrayIcon(img, item);
      tray.appendChild(img); // заодно ставит в нужном порядке
    }
    for (const [id, img] of trayElements) {
      if (!seen.has(id)) { img.remove(); trayElements.delete(id); }
    }
    fitTray();
  }

  /** Иконки, которым не хватает места перед «островом», уходят в скрытый раздел под стрелочкой. */
  function fitTray() {
    const tray = $('tray'), more = $('tray-more');
    for (const img of trayElements.values()) img.hidden = false;
    more.hidden = true;
    const needed = trayItems.length * TRAY_SLOT;
    trayHidden = [];
    if (needed > tray.clientWidth + 1) {
      more.hidden = false; // стрелочка сама занимает место — сколько осталось, меряем уже с ней
      const fits = Math.max(0, Math.floor(tray.clientWidth / TRAY_SLOT));
      trayHidden = trayItems.slice(0, trayItems.length - fits).map((item) => item.id);
      for (const id of trayHidden) trayElements.get(id).hidden = true;
    }
  }

  // ---------- действия ----------

  $('tray-more').addEventListener('click', () => BD.send({ type: 'trayOverflow', rect: BD.rect($('tray-more')), ids: trayHidden }));
  window.addEventListener('resize', fitTray);
  $('sys-button').addEventListener('click', () => BD.send({ type: 'openMenu', rect: BD.rect($('sys-button')) }));
  $('quick').addEventListener('click', () => BD.send({ type: 'open', target: 'quick' }));
  $('clock').addEventListener('click', () => BD.send({ type: 'open', target: 'notifications' }));
  // Колесо мыши над индикаторами меняет громкость.
  $('quick').addEventListener('wheel', (event) => BD.send({ type: 'volumeStep', delta: event.deltaY < 0 ? 2 : -2 }), { passive: true });

  BD.on('sys', (data) => { sys = data; renderSys(); });
  BD.on('tray', renderTray);
  BD.on('state', () => { renderSys(); renderClock(); });
  setInterval(renderClock, 1000);
}
