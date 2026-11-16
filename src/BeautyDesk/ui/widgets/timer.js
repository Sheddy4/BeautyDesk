// Виджет «Таймер»: обратный отсчёт на выбранное число минут. По окончании мигает и подаёт сигнал.
BD.widget('timer', {
  title: 'widget.timer',
  sizes: [[4, 2], [2, 2]],
  create(body, context) {
    const PRESETS = [5, 15, 25, 45]; // минуты

    body.innerHTML = `
      <div class="timer-time"></div>
      <div class="timer-presets"></div>
      <div class="timer-actions"><button class="plain" data-do="toggle"></button><button class="plain" data-do="reset"></button></div>`;
    const display = body.querySelector('.timer-time');
    const presets = body.querySelector('.timer-presets');

    // Состояние хранится в настройках виджета, чтобы отсчёт переживал перезапуск программы:
    // total — на сколько секунд заведён; end — когда закончится (если идёт); left — сколько осталось (если на паузе).
    const read = () => ({
      total: Number(context.options.total ?? 25 * 60),
      end: Number(context.options.end ?? 0),
      left: Number(context.options.left ?? context.options.total ?? 25 * 60),
    });
    const write = (state) => context.setOptions({ total: String(state.total), end: String(state.end), left: String(state.left) });
    let finished = false;

    function remaining() {
      const state = read();
      return state.end > 0 ? Math.max(0, Math.round((state.end - Date.now()) / 1000)) : state.left;
    }

    function beep() {
      try {
        const audio = new AudioContext();
        [0, 0.35, 0.7].forEach((start) => {
          const tone = audio.createOscillator(), gain = audio.createGain();
          tone.frequency.value = 880;
          gain.gain.setValueAtTime(0.15, audio.currentTime + start);
          gain.gain.exponentialRampToValueAtTime(0.001, audio.currentTime + start + 0.25);
          tone.connect(gain).connect(audio.destination);
          tone.start(audio.currentTime + start);
          tone.stop(audio.currentTime + start + 0.26);
        });
        setTimeout(() => audio.close(), 1500);
      } catch { /* звук — не главное */ }
    }

    function render() {
      const state = read();
      const left = remaining();
      if (state.end > 0 && left === 0) {
        // Время вышло: останавливаемся на нуле и привлекаем внимание.
        write({ total: state.total, end: 0, left: 0 });
        if (!finished) { finished = true; beep(); }
      }
      display.textContent = `${String(Math.floor(left / 60)).padStart(2, '0')}:${String(left % 60).padStart(2, '0')}`;
      body.classList.toggle('finished', finished && left === 0);
      body.classList.toggle('running', state.end > 0);
      body.querySelector('[data-do="toggle"]').textContent = BD.t(state.end > 0 ? 'widget.timer.pause' : 'widget.timer.start');
      body.querySelector('[data-do="reset"]').textContent = BD.t('widget.timer.reset');
      for (const button of presets.children) button.classList.toggle('active', Number(button.dataset.minutes) * 60 === state.total);
    }

    for (const minutes of PRESETS) {
      const button = document.createElement('button');
      button.className = 'plain';
      button.dataset.minutes = minutes;
      button.textContent = minutes;
      button.addEventListener('click', () => { finished = false; write({ total: minutes * 60, end: 0, left: minutes * 60 }); render(); });
      presets.appendChild(button);
    }
    body.querySelector('[data-do="toggle"]').addEventListener('click', () => {
      const state = read();
      finished = false;
      if (state.end > 0) write({ total: state.total, end: 0, left: remaining() });                       // пауза
      else write({ total: state.total, end: Date.now() + (state.left || state.total) * 1000, left: 0 }); // пуск
      render();
    });
    body.querySelector('[data-do="reset"]').addEventListener('click', () => {
      const state = read();
      finished = false;
      write({ total: state.total, end: 0, left: state.total });
      render();
    });

    render();
    return { update: render };
  },
});
