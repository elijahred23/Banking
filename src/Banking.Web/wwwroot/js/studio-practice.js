(() => {
    'use strict';
    document.querySelectorAll('[data-studio-practice]').forEach(root => {
        const find = name => root.querySelector(`[data-practice-${name}]`);
        const cases = JSON.parse(find('data').textContent);
        const money = value => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
        const perspectives = {
            maker: 'Maker: verify the beneficiary, amount, authorization, and reference before submitting. You cannot approve your own instruction.',
            approver: 'Approver: independently review the instruction and supporting evidence before release. Approval does not prove settlement.',
            originator: 'Originating bank: correlate the instruction, outgoing message, hold, settlement debit, and customer journal.',
            receiver: 'Receiving bank: match the incoming instruction to the beneficiary and inspect your own acceptance and posting evidence. A sender-side event alone is insufficient.',
            compliance: 'Compliance: inspect screening evidence and record an authorized clear/reject decision. A simulated perspective grants no application permissions.',
            operations: 'Operations: reconcile message outcomes, settlement, and journals. Investigate gaps using correlation IDs; avoid creating duplicate payments.'
        };
        let selected = cases[0], index = 0;
        const answers = new Map();
        const key = () => `${selected.id}:${index}`;
        function line(container, label, value) {
            const dt = document.createElement('dt'), dd = document.createElement('dd');
            dt.textContent = label; dd.textContent = value; container.append(dt, dd);
        }
        function render() {
            const step = selected.steps[index];
            find('objective').textContent = `Objective: ${selected.objective}`;
            find('perspective').textContent = perspectives[find('persona').value];
            find('title').textContent = `${index + 1}. ${step.title}`;
            find('evidence').textContent = step.evidence;
            find('message').textContent = step.message;
            find('question').textContent = step.question;
            find('timeline').replaceChildren();
            selected.steps.forEach((item, i) => {
                const li = document.createElement('li');
                li.textContent = i <= index ? item.title : 'Upcoming event';
                if (i === index) li.setAttribute('aria-current', 'step');
                find('timeline').append(li);
            });
            find('choices').replaceChildren();
            step.choices.forEach((choice, i) => {
                const label = document.createElement('label'), radio = document.createElement('input');
                label.className = 'd-block mb-2';
                radio.type = 'radio'; radio.name = 'practice-answer'; radio.value = String(i); radio.className = 'me-2';
                radio.checked = answers.get(key())?.choice === i;
                label.append(radio, document.createTextNode(choice)); find('choices').append(label);
            });
            find('feedback').textContent = answers.get(key())?.feedback ?? '';
            find('back').disabled = index === 0;
            find('next').disabled = index === selected.steps.length - 1 || !answers.has(key());
            find('balances').replaceChildren();
            line(find('balances'), 'Opening posted balance', money(1000));
            line(find('balances'), 'Current posted balance', money(1000 - step.posted));
            line(find('balances'), 'Current hold', money(step.held));
            line(find('balances'), 'Available balance', money(1000 - step.posted - step.held));
            line(find('balances'), 'Net settlement cash movement', money(-step.posted));
            const hasSettled = index >= 4 && selected.id !== 'compliance';
            const reversed = selected.id === 'return' && index === 5;
            find('journal').textContent = hasSettled
                ? `Original sender journal: debit customer deposits $125; credit settlement cash $125. Debits = credits. ${reversed ? 'Return journal: debit settlement cash $125; credit customer deposits $125. Both journals remain in the audit trail.' : 'Receiving-bank example: debit settlement cash $125; credit beneficiary deposits $125. No second posting for duplicate delivery.'}`
                : 'No posted journal at this event. A hold affects availability, not the posted balance.';
            const attempts = selected.steps.map((_, i) => answers.get(`${selected.id}:${i}`)).filter(Boolean);
            find('score').textContent = `${attempts.length} / ${selected.steps.length} predictions checked; ${attempts.filter(x => x.correct).length} correct. Local practice only. Check a prediction to advance; retry or revisit any event.`;
        }
        find('check').addEventListener('click', () => {
            const input = root.querySelector('input[name="practice-answer"]:checked');
            if (!input) { find('feedback').textContent = 'Choose a prediction first.'; return; }
            const step = selected.steps[index], choice = Number(input.value), correct = choice === step.answer;
            answers.set(key(), { choice, correct, feedback: `${correct ? 'Correct.' : 'Review your prediction.'} ${step.explanation}` });
            render();
        });
        find('case').addEventListener('change', () => { selected = cases.find(x => x.id === find('case').value); index = 0; render(); });
        find('persona').addEventListener('change', render);
        find('back').addEventListener('click', () => { if (index > 0) { index--; render(); } });
        find('next').addEventListener('click', () => { if (answers.has(key()) && index < selected.steps.length - 1) { index++; render(); } });
        find('reset').addEventListener('click', () => { selected.steps.forEach((_, i) => answers.delete(`${selected.id}:${i}`)); index = 0; render(); });
        function clock() {
            const day = find('day').value, after = find('cutoff').value === 'after';
            const wait = day === 'saturday' || day === 'sunday' || after;
            find('clock').textContent = wait
                ? `In this simplified business-window model, processing waits until ${day === 'friday' || day === 'saturday' || day === 'sunday' ? 'Monday' : 'the next weekday'}. Advancing a clock makes processing eligible; it does not prove acceptance or settlement.`
                : 'Eligible for the current fictional processing window. Eligibility is not delivery, acceptance, or settlement. ACH still requires batching/file processing; wire behavior depends on the selected rail.';
        }
        find('day').addEventListener('change', clock); find('cutoff').addEventListener('change', clock);
        root.querySelectorAll('[data-practice-field]').forEach(button => {
            button.addEventListener('click', () => {
                root.querySelectorAll('[data-practice-field]').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
                root.querySelectorAll('[data-practice-field-row]').forEach(row => row.classList.toggle('table-active', row.dataset.practiceFieldRow === button.dataset.practiceField));
                find('field-status').textContent = `Highlighted message mapping for ${button.textContent}.`;
            });
        });
        render(); clock();
    });
})();
