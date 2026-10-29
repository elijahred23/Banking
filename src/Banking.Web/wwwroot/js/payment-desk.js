(() => {
    const root = document.querySelector('[data-payment-desk]');
    if (!root) return;
    const rows = [...root.querySelectorAll('[data-desk-item]')];
    const search = root.querySelector('[data-desk-search]');
    const rail = root.querySelector('[data-desk-rail]');
    const category = root.querySelector('[data-desk-category]');
    const more = root.querySelector('[data-desk-more]');
    let limit = 12;
    const render = () => {
        const query = search.value.trim().toLocaleLowerCase();
        let matches = 0;
        rows.forEach(row => {
            const match = (!query || row.textContent.toLocaleLowerCase().includes(query))
                && (rail.value === 'all' || (rail.value === 'wire' ? row.dataset.rail !== 'ACH' : row.dataset.rail === rail.value))
                && (category.value === 'all' || row.dataset.category === category.value);
            if (match) matches += 1;
            row.hidden = !match || matches > limit;
        });
        root.querySelector('[data-desk-count]').textContent = `${Math.min(matches, limit)} of ${matches} matching payments shown`;
        root.querySelector('[data-desk-empty]').hidden = matches !== 0 || rows.length === 0;
        more.hidden = matches <= limit;
    };
    [search, rail, category].forEach(control => control.addEventListener('input', () => { limit = 12; render(); }));
    more.addEventListener('click', () => { limit += 24; render(); });
    render();

    const cases = [
        { title: 'A wire response never arrives', evidence: 'The original instruction was delivered. No final status is recorded. The customer asks you to send it again.', options: ['Send a new payment now', 'Investigate the original correlation and settlement evidence', 'Mark the original rejected'], correct: 1, explanation: 'Delivery and timeout do not establish the final business outcome. Investigate the original identifiers, status, and accounting before another instruction risks a duplicate.', concepts: ['status', 'settlement', 'duplicate', 'correlation', 'journal'] },
        { title: 'Payroll is approved but has not moved', evidence: 'All entries are batched. Their effective date is the next banking day. The shared lab clock is still on the prior date. A draft NACHA file is available.', options: ['Advance the shared date when ready, then observe the cutoff', 'Recreate the payroll run', 'Treat the draft as operator acceptance'], correct: 0, explanation: 'The effective date makes the batch eligible. The ACH worker must still build and submit the saved file. A draft is inspection evidence, not acceptance or settlement.', concepts: ['effective', 'clock', 'cutoff', 'file', 'settlement'] },
        { title: 'A settled utility collection is returned', evidence: 'The original ACH debit has a balanced posting journal. An RDFI return arrives with its original trace and a separate reversing journal.', options: ['Delete the original journal', 'Collect again immediately', 'Reconcile both journals and investigate the return reason'], correct: 2, explanation: 'Keep the original and reversing movement auditable. The reason and authorization evidence determine a safe next action; a return is distinct from a file rejection.', concepts: ['return', 'journal', 'trace', 'authorization', 'reason'] },
        { title: 'The same payroll file appears twice', evidence: 'Two deliveries contain the same saved file identifier and traces. One settlement result and its journals already exist.', options: ['Inspect duplicate/inbox evidence and confirm one business posting', 'Approve a second settlement', 'Change the traces to force processing'], correct: 0, explanation: 'A transport retry must not create another business payment. Correlate the file, traces, consumer evidence, and journals. A genuinely new customer file needs its own duplicate investigation.', concepts: ['duplicate', 'file', 'trace', 'journal', 'inbox'] },
        { title: 'Cancellation arrives after wire settlement', evidence: 'Settlement identifiers and a balanced value journal are recorded. The sender requests cancellation.', options: ['Erase the settled payment', 'Use the authorized return case and reconcile any separate return', 'Assume the request itself refunds the customer'], correct: 1, explanation: 'A cancellation request does not reverse final settlement. Follow the case response and any accepted return as separate evidence and accounting.', concepts: ['settlement', 'return', 'case', 'journal', 'response'] },
        { title: 'An account correction arrives', evidence: 'The original ACH entry has a notification of change. The next payroll instruction still uses the old receiving account.', options: ['Rewrite the original saved NACHA file', 'Treat the NOC as an automatic refund', 'Review corrected data for future instructions and retain the original'], correct: 2, explanation: 'An NOC communicates correction data. Preserve the original trace and saved file, inspect settlement separately, and review future instructions using the correction workflow.', concepts: ['correction', 'future', 'original', 'trace', 'settlement'] }
    ];
    const practice = root.querySelector('[data-decision-practice]');
    const label = document.createElement('label'); label.htmlFor = 'desk-scenario'; label.textContent = 'Choose an exercise';
    const select = document.createElement('select'); select.id = 'desk-scenario'; select.className = 'form-select mb-3';
    cases.forEach((item, index) => select.add(new Option(item.title, index)));
    const body = document.createElement('div');
    practice.append(label, select, body);
    const showCase = () => {
        const item = cases[Number(select.value)]; body.replaceChildren();
        const evidence = document.createElement('p'); evidence.className = 'desk-practice-evidence'; evidence.textContent = item.evidence;
        const form = document.createElement('form');
        const fieldset = document.createElement('fieldset'); const legend = document.createElement('legend'); legend.className = 'h6'; legend.textContent = 'Your next action'; fieldset.append(legend);
        item.options.forEach((option, index) => {
            const wrapper = document.createElement('label'); wrapper.className = 'desk-choice';
            const input = document.createElement('input'); input.type = 'radio'; input.name = 'decision'; input.value = index; input.required = true;
            wrapper.append(input, document.createTextNode(option)); fieldset.append(wrapper);
        });
        const noteLabel = document.createElement('label'); noteLabel.htmlFor = 'desk-reason'; noteLabel.textContent = 'Which evidence supports your decision?'; noteLabel.className = 'form-label mt-3';
        const note = document.createElement('textarea'); note.id = 'desk-reason'; note.className = 'form-control'; note.required = true; note.minLength = 15; note.maxLength = 1000;
        const button = document.createElement('button'); button.type = 'submit'; button.className = 'btn btn-primary mt-3'; button.textContent = 'Check decision & explanation';
        const feedback = document.createElement('div'); feedback.setAttribute('role', 'status'); feedback.className = 'mt-3';
        form.append(fieldset, noteLabel, note, button, feedback); body.append(evidence, form);
        form.addEventListener('submit', event => {
            event.preventDefault();
            const chosen = Number(new FormData(form).get('decision'));
            const concepts = item.concepts.filter(word => note.value.toLowerCase().includes(word));
            feedback.textContent = `${chosen === item.correct ? 'Appropriate next action.' : 'Reconsider this action.'} ${item.explanation} Evidence terms recognized: ${concepts.length ? concepts.join(', ') : 'none yet'}. This is a visible keyword prompt for self-review, not a competency score.`;
        });
    };
    select.addEventListener('change', showCase); showCase();
})();
