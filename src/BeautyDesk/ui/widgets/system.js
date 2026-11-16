// Два небольших системных виджета: «Диски» (сколько места свободно) и «Сеть» (скорость загрузки и отдачи).

BD.widget('disks', {
  title: 'widget.disks',
  sizes: [[4, 2], [2, 2]],
  create(body) {
    let disks = [];

    function render() {
      body.replaceChildren();
      for (const disk of disks) {
        const used = disk.totalGb > 0 ? Math.round(((disk.totalGb - disk.freeGb) / disk.totalGb) * 100) : 0;
        const row = document.createElement('div');
        row.className = 'hw-row';
        row.classList.toggle('high', used >= 90); // места почти не осталось
        row.innerHTML = '<span class="hw-label"></span><span class="hw-track"><i></i></span><span class="hw-value"></span>';
        row.querySelector('.hw-label').textContent = disk.name;
        row.querySelector('i').style.width = `${used}%`;
        row.querySelector('.hw-value').textContent = `${disk.freeGb} ${BD.t('widget.hardware.gb')} ${BD.t('widget.disks.free')}`;
        body.appendChild(row);
      }
    }

    const onData = (data) => { disks = data; render(); };
    BD.on('disks', onData);
    return { update: render, destroy() { BD.off('disks', onData); } };
  },
});

BD.widget('network', {
  title: 'widget.network',
  sizes: [[4, 2], [2, 2]],
  create(body) {
    const HISTORY = 40; // столько последних замеров рисуется на графике
    body.innerHTML = `
      <div class="net-speeds">
        <div><span class="net-arrow">↓</span><span class="net-down"></span></div>
        <div><span class="net-arrow">↑</span><span class="net-up"></span></div>
      </div>
      <svg class="net-graph" viewBox="0 0 100 30" preserveAspectRatio="none"><polyline fill="none" stroke="currentColor" stroke-width="1.2" vector-effect="non-scaling-stroke"/></svg>`;
    const history = [];
    let last = { down: 0, up: 0 };

    function speed(bytes) {
      const units = BD.t('widget.network.units').split(',');
      if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} ${units[2]}`;
      if (bytes >= 1024) return `${Math.round(bytes / 1024)} ${units[1]}`;
      return `${Math.round(bytes)} ${units[0]}`;
    }

    function render() {
      body.querySelector('.net-down').textContent = speed(last.down);
      body.querySelector('.net-up').textContent = speed(last.up);
      // График загрузки: высота подстраивается под самый быстрый из недавних замеров.
      const peak = Math.max(1024, ...history);
      const points = history.map((value, i) => `${(i / (HISTORY - 1)) * 100},${30 - (value / peak) * 28}`).join(' ');
      body.querySelector('polyline').setAttribute('points', points);
    }

    const onData = (data) => {
      last = data;
      history.push(data.down);
      if (history.length > HISTORY) history.shift();
      render();
    };
    BD.on('net', onData);
    render();
    return { update: render, destroy() { BD.off('net', onData); } };
  },
});
