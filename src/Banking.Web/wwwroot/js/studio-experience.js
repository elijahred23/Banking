(() => {
    'use strict';
    const studio = document.querySelector('.traffic-studio[data-studio-mode]');
    if (!studio) return;
    const preferenceKey = `banking.studio.mode.${studio.dataset.studioKind}`;
    const validModes = new Set(['simulation', 'learning']);
    let savedMode;
    try { savedMode = localStorage.getItem(preferenceKey); } catch (_) { /* Storage is optional. */ }
    const requestedMode = new URLSearchParams(location.search).get('mode');
    function setMode(mode) {
        studio.dataset.studioMode = mode;
        studio.querySelectorAll('[data-studio-mode-choice]').forEach(button => {
            const selected = button.dataset.studioModeChoice === mode;
            button.setAttribute('aria-pressed', String(selected));
            button.classList.toggle('btn-primary', selected);
            button.classList.toggle('btn-outline-primary', !selected);
        });
        studio.querySelector('[data-studio-mode-status]').textContent = mode === 'learning'
            ? 'Guided learning: follow the live observation guide, inspect payment evidence, and use the practice bench after the live board.'
            : 'Simulation mode: live traffic and operations first. Switch to Guided learning for explanations and practice.';
        try { localStorage.setItem(preferenceKey, mode); } catch (_) { /* Keep the current page usable. */ }
    }
    setMode(validModes.has(requestedMode) ? requestedMode : validModes.has(savedMode) ? savedMode : 'simulation');
    studio.querySelectorAll('[data-studio-mode-choice]').forEach(button => {
        button.addEventListener('click', () => setMode(button.dataset.studioModeChoice));
    });
    studio.querySelectorAll('[data-studio-pace]').forEach(button => {
        button.addEventListener('click', () => {
            const rate = studio.querySelector('.traffic-controls select[name="rate"]');
            if (!rate) return;
            rate.value = button.dataset.studioPace;
            rate.dispatchEvent(new Event('change', { bubbles: true }));
            studio.querySelector('[data-studio-pace-status]').textContent = `${button.textContent} selected: ${rate.selectedOptions[0].textContent}. Use Start / update to apply this pace.`;
        });
    });
    let filter = 'all', showAll = false;
    function applyFilter() {
        const desk = studio.querySelector('[data-studio-operations]');
        if (!desk) return;
        desk.querySelector('[data-studio-filter]').value = filter;
        const items = [...desk.querySelectorAll('[data-studio-work-item]')];
        const matches = items.filter(item => filter === 'all' || item.dataset.bucket === filter);
        const visible = new Set(showAll ? matches : matches.slice(0, 8));
        items.forEach(item => { item.hidden = !visible.has(item); });
        desk.querySelector('[data-studio-filter-result]').textContent = matches.length
            ? `Showing ${visible.size} of ${matches.length} matching items in this recent snapshot.`
            : 'No matching items in this recent snapshot. Try another filter or inspect the full payment inquiry.';
        const more = desk.querySelector('[data-studio-show-all]');
        more.hidden = matches.length <= 8;
        more.textContent = showAll ? 'Show first 8 matches' : 'Show all matches';
    }
    // Delegate handlers because the live workload desk is replaced with each snapshot.
    studio.addEventListener('change', event => {
        if (event.target.matches('[data-studio-filter]')) {
            filter = event.target.value; showAll = false; applyFilter();
        }
    });
    studio.addEventListener('click', event => {
        if (event.target.closest('[data-studio-show-all]')) { showAll = !showAll; applyFilter(); }
    });
    studio.addEventListener('studio:snapshot', applyFilter);
    applyFilter();
})();
