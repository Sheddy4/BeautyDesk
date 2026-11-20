// Окно настроек. Значения присылает программа (тема данных 'settings'); страница рисует их
// и сообщает об изменениях. Каждая настройка применяется сразу, кнопки «Сохранить» нет.
if (BD.role === 'settings') {
  const root = document.getElementById('settings-body');
  let data = null;

  const set = (key, value) => BD.send({ type: 'setting', key, value });

  function section(titleKey, ...rows) {
    const box = document.createElement('section');
    const title = document.createElement('h2');
    title.textContent = BD.t(titleKey);
    box.append(title, ...rows);
    return box;
  }

  function row(labelKey, control, hintKey) {
    const line = document.createElement('div');
    line.className = 'settings-row';
    const text = document.createElement('div');
    text.className = 'settings-label';
    text.textContent = BD.t(labelKey);
    if (hintKey) {
      const hint = document.createElement('div');
      hint.className = 'settings-hint';
      hint.textContent = BD.t(hintKey);
      text.appendChild(hint);
    }
    line.append(text, control);
    return line;
  }

  /** Выключатель для настройки «да/нет». */
  function toggle(key) {
    const button = document.createElement('button');
    button.className = 'switch';
    button.classList.toggle('on', Boolean(data[key]));
    button.addEventListener('click', () => set(key, !data[key]));
    return button;
  }

  /** Несколько взаимоисключающих вариантов в ряд. */
  function choice(key, options) {
    const group = document.createElement('div');
    group.className = 'choice';
    for (const option of options) {
      const button = document.createElement('button');
      button.textContent = option.label;
      button.classList.toggle('active', data[key] === option.value);
      button.addEventListener('click', () => set(key, option.value));
      group.appendChild(button);
    }
    return group;
  }

  /** Выпадающий список. */
  function select(key, options) {
    const el = document.createElement('select');
    el.className = 'settings-select';
    for (const option of options) {
      const o = document.createElement('option');
      o.value = option.value;
      o.textContent = option.label;
      o.selected = option.value === key.value;
      el.appendChild(o);
    }
    el.addEventListener('change', () => set(key.name, el.value));
    return el;
  }

  function button(labelKey, onClick) {
    const b = document.createElement('button');
    b.className = 'settings-button';
    b.textContent = BD.t(labelKey);
    b.addEventListener('click', onClick);
    return b;
  }

  function render() {
    if (!data) return;
    document.getElementById('settings-title').textContent = BD.t('settings.title');

    const wallpaper = document.createElement('div');
    wallpaper.className = 'settings-wallpaper';
    wallpaper.append(choice('wallpaperMode', [
      { value: 'theme', label: BD.t('settings.wallpaper.theme') },
      { value: 'custom', label: BD.t('settings.wallpaper.custom') },
      { value: 'none', label: BD.t('settings.wallpaper.none') },
    ]));
    if (data.wallpaperMode === 'custom') {
      const pick = document.createElement('button');
      pick.className = 'settings-button';
      pick.textContent = data.wallpaper ? data.wallpaper.split(/[\\/]/).pop() : BD.t('settings.wallpaper.choose');
      pick.title = data.wallpaper ?? '';
      pick.addEventListener('click', () => BD.send({ type: 'chooseWallpaper' }));
      wallpaper.appendChild(pick);
    }

    root.replaceChildren(
      section('settings.appearance',
        row('settings.theme', choice('theme', data.themes.map((t) => ({ value: t.id, label: t.name })))),
        row('settings.language', choice('language', data.languages.map((l) => ({ value: l, label: l.toUpperCase() })))),
        row('settings.wallpaper', wallpaper, 'settings.wallpaper.hint')),
      section('settings.behavior',
        row('settings.autostart', toggle('autostart')),
        row('settings.desktopIcons', toggle('showDesktopIcons'))),
      section('settings.bar',
        row('settings.showIsland', toggle('showIsland')),
        row('settings.showTray', toggle('showTray')),
        row('settings.showLayout', toggle('showLayout')),
        row('settings.showDesktops', toggle('showDesktops'))),
      section('settings.corners', ...['topLeft', 'topRight', 'bottomLeft', 'bottomRight'].map((corner) =>
        row(`settings.corner.${corner}`, select({ name: `corner.${corner}`, value: data.hotCorners?.[corner] ?? 'none' },
          ['none', 'desktop', 'taskview', 'launcher', 'lock', 'widgets'].map((a) => ({ value: a, label: BD.t(`settings.cornerAction.${a}`) })))))),
      section('settings.widgets',
        row('settings.userWidgets', button('settings.userWidgets.open', () => BD.send({ type: 'openWidgetsFolder' })), 'settings.userWidgets.hint')),
      section('settings.dockLauncher',
        row('settings.showTrash', toggle('showTrash')),
        row('settings.systemApps', toggle('launcherSystemApps'), 'settings.systemApps.hint')),
    );
    document.getElementById('settings-version').textContent = `BeautyDesk ${data.version}`;
  }

  document.getElementById('settings-close').addEventListener('click', () => BD.send({ type: 'settingsClose' }));
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape') BD.send({ type: 'settingsClose' }); });

  BD.on('settings', (value) => { data = value; render(); });
  BD.on('state', render);
}
