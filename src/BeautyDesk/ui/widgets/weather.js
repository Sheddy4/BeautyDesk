// Виджет «Погода»: текущая погода и прогноз на ближайшие дни для города, который указал человек.
(() => {
  const svg = (body) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;
  const CLOUD = '<path d="M7 18h10a4 4 0 0 0 .6-7.96A6 6 0 0 0 6.1 11.6 3.3 3.3 0 0 0 7 18z"/>';
  const ICONS = {
    clear: svg('<circle cx="12" cy="12" r="4"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M5.6 18.4L7 17M17 7l1.4-1.4"/>'),
    night: svg('<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/>'),
    partly: svg('<circle cx="8" cy="8" r="3"/><path d="M8 2v1.5M2 8h1.5M3.8 3.8l1 1M12.2 3.8l-1 1"/><path d="M10 20h7.5a3.2 3.2 0 0 0 .4-6.38A4.8 4.8 0 0 0 9 14.9 2.6 2.6 0 0 0 10 20z"/>'),
    cloudy: svg(CLOUD),
    fog: svg('<path d="M4 9h16M4 13h16M6 17h12"/>'),
    rain: svg(CLOUD.replace('18h10', '15h10').replace('18z', '15z') + '<path d="M9 18l-1 3M13 18l-1 3M17 18l-1 3"/>'),
    snow: svg(CLOUD.replace('18h10', '15h10').replace('18z', '15z') + '<path d="M9 19v.01M12 21v.01M15 19v.01"/>'),
    thunder: svg(CLOUD.replace('18h10', '15h10').replace('18z', '15z') + '<path d="M13 16l-2 3h3l-2 3"/>'),
  };

  /** Код погоды ВМО -> вид погоды (он же ключ перевода и иконки). */
  function kind(code, isDay = true) {
    if (code === 0) return isDay ? 'clear' : 'night';
    if (code <= 2) return 'partly';
    if (code === 3) return 'cloudy';
    if (code <= 48) return 'fog';
    if (code <= 67 || (code >= 80 && code <= 82)) return 'rain';
    if (code <= 77 || code === 85 || code === 86) return 'snow';
    return 'thunder';
  }

  BD.widget('weather', {
    title: 'widget.weather',
    sizes: [[4, 2], [2, 2]],
    editable: true,
    create(body, context) {
      let report = null;
      let asking = false;
      let timer = null;

      function request() {
        const city = context.options.city;
        if (city) BD.send({ type: 'weather', city });
      }

      /** Поле для ввода города. Показывается, пока город не задан, и по кнопке с карандашом в режиме расстановки. */
      function renderForm() {
        body.innerHTML = `<div class="weather-form"><div class="muted"></div><input type="text" spellcheck="false"><button class="plain"></button></div>`;
        body.querySelector('.muted').textContent = BD.t('widget.weather.askCity');
        const input = body.querySelector('input');
        const button = body.querySelector('button');
        input.value = context.options.city ?? '';
        input.placeholder = BD.t('widget.weather.cityPlaceholder');
        button.textContent = BD.t('widget.weather.save');
        // Окно виджетов обычно не берёт клавиатуру — просим её у программы, когда человек собирается печатать.
        input.addEventListener('pointerdown', () => BD.send({ type: 'focus' }));
        const submit = () => {
          const city = input.value.trim();
          if (!city) return;
          asking = false;
          report = null;
          context.setOptions({ city });
          render();
          request();
        };
        button.addEventListener('click', submit);
        input.addEventListener('keydown', (event) => { if (event.key === 'Enter') submit(); });
      }

      function render() {
        body.classList.toggle('configuring', asking || !context.options.city);
        if (asking || !context.options.city) { renderForm(); return; }
        if (!report) { body.innerHTML = `<div class="muted">${context.options.city}…</div>`; return; }
        if (report.error) {
          body.innerHTML = '<div class="weather-error"><div></div><div class="muted"></div></div>';
          body.firstChild.children[0].textContent = context.options.city;
          body.firstChild.children[1].textContent = BD.t(`widget.weather.${report.error}`);
          return;
        }

        const language = BD.state?.language ?? 'en';
        const weekday = new Intl.DateTimeFormat(language, { weekday: 'short' });
        const now = kind(report.code, report.isDay);
        const days = report.days.slice(1).map((day) => `
          <div class="weather-day">
            <span class="muted">${weekday.format(new Date(`${day.date}T12:00:00`))}</span>
            <span class="weather-icon">${ICONS[kind(day.code)]}</span>
            <span>${day.max}° <span class="muted">${day.min}°</span></span>
          </div>`).join('');
        body.innerHTML = `
          <div class="weather-now">
            <span class="weather-icon big">${ICONS[now]}</span>
            <div>
              <div class="weather-temp">${report.temperature}°</div>
              <div class="weather-city"></div>
            </div>
            <div class="weather-details muted">
              <div class="weather-kind"></div>
              <div>${BD.t('widget.weather.feels')} ${report.feelsLike}°</div>
              <div>${BD.t('widget.weather.wind')} ${report.wind} ${BD.t('widget.weather.ms')} · ${report.humidity}%</div>
            </div>
          </div>
          <div class="weather-days">${days}</div>`;
        body.querySelector('.weather-city').textContent = report.name;
        body.querySelector('.weather-kind').textContent = BD.t(`widget.weather.${now}`);
      }

      const onWeather = (data) => {
        // Ответ общий для всех виджетов погоды — берём только свой город.
        if (data.query !== context.options.city) return;
        report = data;
        if (!asking) render();
      };
      BD.on('weather', onWeather);
      render();
      request();
      timer = setInterval(request, 20 * 60 * 1000);

      let language = BD.state?.language;
      return {
        update() {
          // Перерисовка нужна только при смене языка; тогда же названия города и дней запрашиваются заново.
          if (BD.state?.language === language) return;
          language = BD.state?.language;
          render();
          request();
        },
        edit() { asking = true; render(); },
        destroy() {
          clearInterval(timer);
          BD.off('weather', onWeather);
        },
      };
    },
  });
})();
