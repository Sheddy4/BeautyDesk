// Виджет «Календарь»: текущий месяц сеткой, сегодняшний день выделен.
BD.widget('calendar', {
  title: 'widget.calendar',
  sizes: [[4, 4], [3, 3]],
  create(body) {
    const title = document.createElement('div');
    title.className = 'calendar-title';
    const grid = document.createElement('div');
    grid.className = 'calendar-grid';
    body.append(title, grid);
    let shown = '';

    return {
      update() {
        const now = new Date();
        const language = BD.state?.language ?? 'en';
        // Перерисовывается только при смене дня или языка, а не каждую секунду.
        const key = `${now.toDateString()}|${language}`;
        if (key === shown) return;
        shown = key;

        title.textContent = new Intl.DateTimeFormat(language, { month: 'long', year: 'numeric' }).format(now);
        grid.replaceChildren();

        // Неделя начинается с понедельника; 1 января 2024 года — понедельник, от него берутся названия дней.
        const weekday = new Intl.DateTimeFormat(language, { weekday: 'short' });
        for (let i = 0; i < 7; i++) {
          const cell = document.createElement('span');
          cell.className = 'calendar-weekday';
          cell.textContent = weekday.format(new Date(2024, 0, 1 + i));
          grid.appendChild(cell);
        }

        const first = new Date(now.getFullYear(), now.getMonth(), 1);
        const blanks = (first.getDay() + 6) % 7;
        const days = new Date(now.getFullYear(), now.getMonth() + 1, 0).getDate();
        for (let i = 0; i < blanks; i++) grid.appendChild(document.createElement('span'));
        for (let day = 1; day <= days; day++) {
          const cell = document.createElement('span');
          cell.className = 'calendar-day';
          cell.textContent = day;
          if (day === now.getDate()) cell.classList.add('today');
          if ((blanks + day - 1) % 7 >= 5) cell.classList.add('weekend');
          grid.appendChild(cell);
        }
      },
    };
  },
});
