// Виджет «Часы»: крупное время и дата под ним.
BD.widget('clock', {
  title: 'widget.clock',
  sizes: [[4, 2], [2, 2], [3, 1]],
  create(body) {
    const time = document.createElement('div');
    time.className = 'clock-time';
    const date = document.createElement('div');
    date.className = 'clock-date';
    body.append(time, date);

    return {
      update() {
        const now = new Date();
        const language = BD.state?.language ?? 'en';
        time.textContent = new Intl.DateTimeFormat(language, { hour: '2-digit', minute: '2-digit' }).format(now);
        date.textContent = new Intl.DateTimeFormat(language, { weekday: 'long', day: 'numeric', month: 'long' }).format(now);
      },
    };
  },
});
