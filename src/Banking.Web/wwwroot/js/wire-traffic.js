(() => {
    const studio = document.querySelector('.traffic-studio');
    if (!studio) return;
    const target = document.getElementById('traffic-snapshot');
    const connection = document.getElementById('traffic-connection');
    let stopped = false;
    async function refresh() {
        if (document.hidden) { schedule(); return; }
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 8000);
        try {
            const response = await fetch(studio.dataset.snapshotUrl, {
                cache: 'no-store', credentials: 'same-origin', signal: controller.signal
            });
            if (!response.ok || response.redirected) throw new Error('Refresh unavailable');
            // Razor encodes every value; no untrusted XML or generated HTML is inserted here.
            const fragment = document.createElement('template');
            fragment.innerHTML = await response.text();
            if (!fragment.content.querySelector('.traffic-context')) throw new Error('Invalid snapshot');
            const open = new Set([...target.querySelectorAll('details[open]')].map(x => x.dataset.wireId));
            const money = new Map([...target.querySelectorAll('[data-money]')].map(x => [x.dataset.money, x.textContent]));
            const focused = target.contains(document.activeElement) ? document.activeElement : null;
            const focusedCard = focused?.closest('details[data-wire-id]');
            const focusKey = focusedCard?.dataset.wireId;
            const focusHref = focused?.closest('a')?.getAttribute('href');
            for (const card of fragment.content.querySelectorAll('details')) card.open = open.has(card.dataset.wireId);
            for (const value of fragment.content.querySelectorAll('[data-money]')) {
                if (money.has(value.dataset.money) && money.get(value.dataset.money) !== value.textContent)
                    value.classList.add('traffic-money-changed');
            }
            target.replaceChildren(fragment.content);
            // Keep live updates running while preserving keyboard focus on the inspected card.
            if (focusKey) {
                const restored = [...target.querySelectorAll('details')].find(x => x.dataset.wireId === focusKey);
                const link = focusHref && [...(restored?.querySelectorAll('a') ?? [])]
                    .find(x => x.getAttribute('href') === focusHref);
                (link || restored?.querySelector('summary'))?.focus({ preventScroll: true });
            }
            connection.textContent = '● Live · refreshes every 1.5s';
            connection.classList.remove('traffic-offline');
        } catch (_) {
            connection.textContent = 'Updates interrupted · reconnecting…';
            connection.classList.add('traffic-offline');
        } finally { clearTimeout(timeout); schedule(); }
    }
    function schedule() { if (!stopped) setTimeout(refresh, 1500); }
    window.addEventListener('pagehide', () => { stopped = true; });
    refresh();
})();
