// Виджет «Микшер»: устройство вывода, общая громкость и громкость каждой программы.
BD.widget('mixer', {
  title: 'widget.mixer',
  sizes: [[4, 3], [4, 4], [4, 2]],
  create(body) {
    body.innerHTML = '<select class="mixer-device"></select><div class="mixer-list"></div>';
    const deviceSelect = body.querySelector('.mixer-device');
    const list = body.querySelector('.mixer-list');
    const rows = new Map(); // id строки -> элементы
    let dragging = null;    // ползунок, который сейчас тянут: его не перерисовываем из присланных данных

    deviceSelect.addEventListener('change', () => BD.send({ type: 'mixerDevice', id: deviceSelect.value }));
    // Список устройств открывается в окне виджетов — ему нужна клавиатура и фокус.
    deviceSelect.addEventListener('pointerdown', () => BD.send({ type: 'focus' }));

    function row(id, name, icon, onVolume, onMute) {
      let r = rows.get(id);
      if (!r) {
        const el = document.createElement('div');
        el.className = 'mixer-row';
        el.innerHTML = '<span class="mixer-icon"></span><div class="mixer-main"><div class="mixer-name"></div><input type="range" min="0" max="100"></div><button class="mixer-mute plain"></button>';
        const slider = el.querySelector('input');
        slider.addEventListener('input', () => { dragging = slider; onVolume(Number(slider.value)); el.querySelector('.mixer-value').textContent = slider.value; });
        slider.addEventListener('change', () => { dragging = null; });
        el.querySelector('.mixer-main').insertAdjacentHTML('beforeend', '<span class="mixer-value"></span>');
        el.querySelector('.mixer-mute').addEventListener('click', () => onMute());
        r = { el, slider };
        rows.set(id, r);
      }
      r.el.querySelector('.mixer-name').textContent = name;
      const iconEl = r.el.querySelector('.mixer-icon');
      if (icon && !iconEl.querySelector('img')) iconEl.innerHTML = `<img src="${icon}" alt="">`;
      else if (!icon) iconEl.textContent = (name || '?').charAt(0).toUpperCase();
      return r;
    }

    function render(data) {
      deviceSelect.replaceChildren(...data.devices.map((d) => {
        const option = document.createElement('option');
        option.value = d.id;
        option.textContent = d.name;
        option.selected = d.default;
        return option;
      }));

      const seen = new Set();
      const ordered = [];
      if (data.master != null) {
        const master = row('master', BD.t('widget.mixer.master'), null,
          (v) => BD.send({ type: 'mixerMaster', volume: v }),
          () => BD.send({ type: 'mixerMaster', volume: Number(master.slider.value), mute: !master.el.classList.contains('muted') }));
        update(master, data.master, data.masterMuted);
        master.el.classList.add('master');
        seen.add('master');
        ordered.push(master.el);
      }
      for (const app of data.apps) {
        const r = row(app.id, app.name, app.iconData,
          (v) => BD.send({ type: 'mixerSet', session: app.id, volume: v }),
          () => BD.send({ type: 'mixerSet', session: app.id, volume: Number(r.slider.value), mute: !r.el.classList.contains('muted') }));
        update(r, app.volume, app.muted);
        r.el.classList.toggle('silent', !app.active);
        seen.add(app.id);
        ordered.push(r.el);
      }
      for (const [id, r] of rows) if (!seen.has(id)) { r.el.remove(); rows.delete(id); }
      ordered.forEach((el) => list.appendChild(el));
    }

    function update(r, volume, muted) {
      if (dragging !== r.slider) {
        r.slider.value = volume;
        r.el.querySelector('.mixer-value').textContent = volume;
      }
      r.el.classList.toggle('muted', muted);
      r.el.querySelector('.mixer-mute').textContent = muted ? '🔇' : '🔊';
    }

    const onData = (data) => render(data);
    BD.on('mixer', onData);
    return { destroy() { BD.off('mixer', onData); } };
  },
});
