// Виджет «Питание»: режим питания Windows (эффективность / баланс / производительность) и батарея.
// Если установлено дополнение производителя (например, ASUS), к этому добавляются режимы вентиляторов,
// обороты и порог зарядки — их присылает канал данных «fans» (см. Services/Bridge.cs и addons/).
(() => {
  const MODES = [
    { id: 'efficiency', key: 'widget.power.efficiency' },
    { id: 'balanced', key: 'widget.power.balanced' },
    { id: 'performance', key: 'widget.power.performance' },
  ];
  const STALE = 15; // секунд без обновления — дополнение не работает
  // Лопасть вентилятора, повёрнутая трижды.
  const BLADE = 'M12 12C10.1 9.4 10.1 4.6 12.6 2.9c2.6-.6 3.6 3.2 2.3 5.9-.6 1.3-1.5 2.4-2.9 3.2z';
  const FAN = `<svg viewBox="0 0 24 24"><g fill="currentColor"><path d="${BLADE}"/><path d="${BLADE}" transform="rotate(120 12 12)"/>`
    + `<path d="${BLADE}" transform="rotate(240 12 12)"/><circle cx="12" cy="12" r="2.4"/></g></svg>`;
  // Скорость вращения значка по режиму: чем мощнее, тем быстрее (секунд на треть оборота).
  const SPIN = { efficiency: 2.6, balanced: 1.9, performance: 1.2 };

  BD.widget('power', {
    title: 'widget.power',
    sizes: [[4, 2], [4, 3], [2, 2]],
    create(body) {
      body.innerHTML = `
        <div class="power-top">
          <span class="power-fan">${FAN}</span>
          <div class="power-title"><div class="power-mode"></div><div class="power-sub muted"></div></div>
          <div class="power-battery"></div>
        </div>
        <div class="power-row windows"></div>
        <div class="power-row vendor"></div>
        <div class="power-row limit"></div>`;
      const $ = (s) => body.querySelector(s);
      let power = null;
      let vendor = null;     // данные дополнения производителя
      let lastSpin = '';

      const chip = (label, active, onClick) => {
        const b = document.createElement('button');
        b.className = 'power-chip';
        b.classList.toggle('on', active);
        b.textContent = label;
        b.addEventListener('click', onClick);
        return b;
      };
      const name = (value) => (typeof value === 'string' ? value : value?.[BD.state?.language] ?? value?.en ?? '');

      function render() {
        // Данные дополнения свежие, только если оно обновляло их недавно; иначе оно не запущено.
        const vendorFresh = vendor?.value && Date.now() - vendor.updatedAt < STALE * 1000;
        const v = vendorFresh ? vendor.value : null;

        // Режим питания Windows.
        const row = $('.power-row.windows');
        row.replaceChildren();
        if (power?.mode) {
          for (const m of MODES) row.appendChild(chip(BD.t(m.key), power.mode === m.id, () => {
            power.mode = m.id; render(); BD.send({ type: 'powerMode', mode: m.id });
          }));
        }

        // Режимы вентиляторов от производителя.
        const vendorRow = $('.power-row.vendor');
        vendorRow.replaceChildren();
        if (v?.modes?.length) {
          for (const m of v.modes) vendorRow.appendChild(chip(name(m.name), v.mode === m.id, () => {
            v.mode = m.id; render(); BD.bridgeSend('fans', { action: 'mode', id: m.id });
          }));
        }
        const limitRow = $('.power-row.limit');
        limitRow.replaceChildren();
        if (v?.chargeLimits?.length) {
          const label = document.createElement('span');
          label.className = 'muted';
          label.textContent = BD.t('widget.power.limit');
          limitRow.appendChild(label);
          for (const value of v.chargeLimits) limitRow.appendChild(chip(`${value}%`, v.chargeLimit === value, () => {
            v.chargeLimit = value; render(); BD.bridgeSend('fans', { action: 'chargeLimit', value });
          }));
        }

        // Заголовок: режим вентиляторов, если он есть, иначе режим Windows.
        const vendorMode = v?.modes?.find((m) => m.id === v.mode);
        const winMode = MODES.find((m) => m.id === power?.mode);
        $('.power-mode').textContent = vendorMode ? name(vendorMode.name) : winMode ? BD.t(winMode.key) : BD.t('widget.power');
        const rpm = (v?.fans ?? []).filter((f) => f.rpm != null).map((f) => `${f.name} ${f.rpm}`).join(' · ');
        $('.power-sub').textContent = rpm ? `${rpm} ${BD.t('widget.power.rpm')}` : vendorMode ? (v.vendor ?? '') : !power?.mode && power ? BD.t('widget.power.unavailable') : '';

        // Батарея.
        const battery = $('.power-battery');
        if (power?.battery != null) {
          const left = power.minutesLeft != null ? `${Math.floor(power.minutesLeft / 60)}:${String(power.minutesLeft % 60).padStart(2, '0')}` : '';
          battery.innerHTML = `<b>${power.battery}%</b><span class="muted">${power.charging ? BD.t('widget.power.charging') : power.plugged ? BD.t('widget.power.plugged') : left}</span>`;
        } else battery.textContent = '';

        // Значок вентилятора крутится шагами (несколько кадров в секунду): плавное вращение
        // в окне во весь экран заметно грузит процессор. Скорость — по мощности режима.
        const mode = vendorMode ? (['efficiency', 'balanced', 'performance'][Math.min(2, v.modes.indexOf(vendorMode))] ?? 'balanced') : power?.mode;
        const third = SPIN[mode] ?? 2;
        const spin = power && !power.plugged && !vendorMode ? 'none' : `power-spin ${third}s steps(${Math.round(third * 5)}) infinite`;
        if (spin !== lastSpin) { lastSpin = spin; $('.power-fan svg').style.animation = spin; }
        body.classList.toggle('has-vendor', Boolean(v));
      }

      const onPower = (data) => { power = data; render(); };
      const onVendor = (data) => { vendor = data; render(); };
      BD.on('power', onPower);
      BD.bridge('fans', onVendor);
      render();

      let language = BD.state?.language;
      return {
        update() {
          // Раз в секунду: смена языка и проверка, не перестало ли дополнение присылать данные.
          const fresh = vendor?.value && Date.now() - vendor.updatedAt < STALE * 1000;
          if (BD.state?.language !== language || Boolean(fresh) !== body.classList.contains('has-vendor')) { language = BD.state?.language; render(); }
        },
        destroy() { BD.off('power', onPower); BD.off('bridge:fans', onVendor); },
      };
    },
  });
})();
