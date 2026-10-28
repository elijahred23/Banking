(() => {
    'use strict';
    const root = document.querySelector('[data-session-replay]');
    if (!root) return;
    const events = JSON.parse(root.querySelector('[data-replay-events]').textContent);
    if (!events.length) return;
    const slider = root.querySelector('[data-replay-position]');
    const format = x => new Intl.NumberFormat('en-US', {style: 'currency', currency: 'USD'}).format(x);
    const sums = []; let debit = 0, credit = 0;
    events.forEach(item => { debit += item.debit; credit += item.credit; sums.push([debit, credit]); });
    function render() {
        const index = Number(slider.value), item = events[index];
        root.querySelector('[data-replay-status]').textContent = `${index + 1} / ${events.length} · ${new Date(item.at).toLocaleString()} · ${item.type}`;
        root.querySelector('[data-replay-content]').textContent = item.text;
        root.querySelector('[data-replay-accounting]').textContent = `Journal lines reached: debits ${format(sums[index][0])}; credits ${format(sums[index][1])}.`;
        root.querySelector('[data-replay-back]').disabled = index === 0;
        root.querySelector('[data-replay-next]').disabled = index === events.length - 1;
        document.querySelectorAll('[data-replay-row]').forEach(row => row.classList.toggle('table-active', Number(row.dataset.replayRow) === index));
    }
    slider.addEventListener('input', render);
    root.querySelector('[data-replay-back]').addEventListener('click', () => { slider.value = Math.max(0, Number(slider.value) - 1); render(); });
    root.querySelector('[data-replay-next]').addEventListener('click', () => { slider.value = Math.min(events.length - 1, Number(slider.value) + 1); render(); });
    render();
})();
