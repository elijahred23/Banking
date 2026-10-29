(() => {
    const kind = document.getElementById('Kind');
    const description = document.querySelector('[data-job-description]');
    if (kind && description) {
        const update = () => { description.textContent = kind.selectedOptions[0]?.dataset.description || ''; };
        kind.addEventListener('change', update); update();
    }
    const pending = document.querySelector('[data-report-status-url]');
    if (!pending) return;
    const progress = pending.querySelector('[data-report-progress]');
    let stopped = false;
    const poll = async () => {
        if (stopped) return;
        try {
            const response = await fetch(pending.dataset.reportStatusUrl, { cache: 'no-store', headers: { Accept: 'application/json' } });
            if (response.status === 401 || response.status === 403 || response.status === 404 || response.redirected) {
                progress.textContent = 'This run is no longer accessible in the current bank/persona session. Open the report library or sign in again.';
                stopped = true; return;
            }
            if (!response.ok) throw new Error('Status unavailable');
            const state = await response.json();
            if (state.complete) { window.location.reload(); return; }
            progress.textContent = `${state.status}. Last checked ${new Date().toLocaleTimeString()}.`;
        } catch { progress.textContent = 'Status check unavailable. Retrying; the durable request remains saved.'; }
        if (!stopped) window.setTimeout(poll, document.hidden ? 15000 : 3000);
    };
    poll();
})();
