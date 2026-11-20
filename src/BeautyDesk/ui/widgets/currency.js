// Виджет «Курсы валют»: сколько стоят выбранные валюты в базовой. Курсы — с бесплатного сервиса
// open.er-api.com (без ключа), обновляются раз в сутки. Валюты меняются кнопкой с карандашом.
BD.widget('currency', {
  title: 'widget.currency',
  sizes: [[4, 2], [2, 2]],
  editable: true,
  create(body, context) {
    // По умолчанию — от языка: по-русски курсы к рублю, иначе к доллару.
    const defaults = () => (BD.state?.language === 'ru' ? { base: 'RUB', list: 'USD EUR CNY' } : { base: 'USD', list: 'EUR GBP JPY' });
    const settings = () => ({ base: context.options.base ?? defaults().base, list: context.options.list ?? defaults().list });
    const codes = (text) => text.toUpperCase().split(/[\s,;]+/).filter((c) => /^[A-Z]{3}$/.test(c)).slice(0, 4);
    let rates = null, editing = false, timer = null;

    const request = async () => {
      const answer = await BD.request('currency', { base: settings().base }, 'currency');
      rates = answer;
      if (!editing) render();
    };

    function render() {
      const { base, list } = settings();
      if (editing) {
        body.innerHTML = `<div class="currency-form">
          <label><span class="muted"></span><input class="base" maxlength="3" spellcheck="false"></label>
          <label><span class="muted"></span><input class="list" spellcheck="false"></label>
          <button class="plain"></button></div>`;
        const [baseLabel, listLabel] = body.querySelectorAll('label .muted');
        baseLabel.textContent = BD.t('widget.currency.base');
        listLabel.textContent = BD.t('widget.currency.list');
        const baseInput = body.querySelector('.base'), listInput = body.querySelector('.list');
        baseInput.value = base;
        listInput.value = list;
        for (const input of [baseInput, listInput]) input.addEventListener('pointerdown', () => BD.send({ type: 'focus' }));
        const submit = () => {
          const b = codes(baseInput.value)[0], l = codes(listInput.value);
          if (!b || !l.length) return;
          context.setOptions({ base: b, list: l.join(' ') });
          editing = false;
          rates = null;
          render();
          request();
        };
        body.querySelector('button').textContent = BD.t('widget.weather.save');
        body.querySelector('button').addEventListener('click', submit);
        listInput.addEventListener('keydown', (event) => { if (event.key === 'Enter') submit(); });
        return;
      }
      if (!rates) { body.innerHTML = '<div class="muted">…</div>'; return; }
      if (rates.error) { body.innerHTML = `<div class="muted"></div>`; body.firstChild.textContent = BD.t(`widget.currency.${rates.error}`); return; }

      const language = BD.state?.language ?? 'en';
      const number = new Intl.NumberFormat(language, { maximumFractionDigits: 2, minimumFractionDigits: 2 });
      body.innerHTML = '<div class="currency-list"></div><div class="currency-date muted"></div>';
      const container = body.querySelector('.currency-list');
      for (const code of codes(list)) {
        const value = rates.values[code];
        const row = document.createElement('div');
        row.className = 'currency-row';
        // Сколько базовой валюты стоит одна единица выбранной.
        row.innerHTML = `<span class="currency-code"></span><span class="currency-value"></span>`;
        row.querySelector('.currency-code').textContent = code;
        row.querySelector('.currency-value').textContent = value ? `${number.format(1 / value)} ${base}` : '—';
        container.appendChild(row);
      }
      body.querySelector('.currency-date').textContent = rates.updatedAt
        ? `${BD.t('widget.currency.updated')} ${new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short' }).format(new Date(rates.updatedAt))}` : '';
    }

    render();
    request();
    timer = setInterval(request, 60 * 60 * 1000);
    let language = BD.state?.language;
    return {
      update() { if (BD.state?.language !== language) { language = BD.state?.language; if (!editing) render(); } },
      edit() { editing = true; render(); },
      destroy() { clearInterval(timer); },
    };
  },
});
