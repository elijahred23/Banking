(() => {
    const money = amount => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount);
    function node(tag, text, className) {
        const result = document.createElement(tag); if (text != null) result.textContent = text;
        if (className) result.className = className; return result;
    }
    document.querySelectorAll('[data-check-storyboard]').forEach(root => {
        const outcome = root.querySelector('[data-check-outcome]');
        const stage = root.querySelector('[data-check-stage]');
        const amountInput = document.querySelector('form [name="Amount"]');
        function render() {
            const amount = amountInput ? Number(amountInput.value) : Number(root.dataset.amount);
            const explanation = root.querySelector('[data-check-explanation]');
            const balances = root.querySelector('[data-check-balances]');
            balances.replaceChildren();
            if (!Number.isFinite(amount) || amount <= 0) {
                explanation.textContent = 'Enter a positive check amount to preview collection.'; return;
            }
            const index = Number(stage.value); const final = index === 3;
            const collected = final && outcome.value === 'collected';
            const titles = ['Captured: provisional credit recorded', 'Bundled into an image cash letter', 'Delivered to simulated image exchange', collected ? 'Collected and posted' : 'Returned; provisional credit removed'];
            const details = [
                'Front and back images plus MICR identify the item. Pending and provisional credit are tracked separately from posted money.',
                'The cash letter groups check images and item data. File construction does not prove collection.',
                'Transport delivery still needs a business outcome. The depositor cannot spend this provisional credit in this lab model.',
                collected ? 'Final acceptance credits the depositor and moves bank settlement cash. A balanced clearing journal preserves both sides.' : 'This branch returns the item before settlement. The original ledger stays unchanged; no settlement journal posts.'
            ];
            stage.setAttribute('aria-valuetext', titles[index]);
            root.querySelectorAll('[data-check-route] li').forEach((item, i) => item.classList.toggle('is-active', i === index));
            explanation.replaceChildren(node('h3', titles[index], 'h6'), node('p', details[index]));
            const pending = final ? 0 : amount;
            const values = [['Ledger', 1000 + (collected ? amount : 0)], ['Available', 1000 + (collected ? amount : 0)], ['Pending credit', pending], ['Provisional credit', pending]];
            for (const [name, value] of values) {
                const card = node('div', null, 'visual-balance'); card.append(node('strong', name), node('span', money(value))); balances.append(card);
            }
        }
        outcome.addEventListener('change', render); stage.addEventListener('input', render); amountInput?.addEventListener('input', render); render();
    });
    document.querySelectorAll('[data-nacha-anatomy]').forEach(root => {
        const records = JSON.parse(root.querySelector('[data-nacha-records]').textContent);
        const tree = root.querySelector('[data-nacha-tree]');
        const inspector = root.querySelector('[data-nacha-record]');
        const file = node('details'); file.open = true;
        file.append(node('summary', `File · ${records.length} records · ${records.filter(r => r.type === '5').length} batches · ${records.filter(r => r.type === '6').length} entries`));
        tree.append(file); let batch = null; let entry = null;
        const buttons = [];
        function inspect(record, selectedButton) {
            buttons.forEach(b => b.setAttribute('aria-pressed', String(b === selectedButton)));
            inspector.replaceChildren(node('h3', `Record ${record.number} · ${record.name}`, 'h6 mt-3'));
            const raw = node('pre', null, 'nacha-record-strip'); const code = node('code'); raw.append(code);
            const fields = node('div', null, 'visual-controls'); const description = node('p'); description.setAttribute('role', 'status');
            function highlight(field) {
                code.replaceChildren();
                if (field) {
                    const start = field.start - 1;
                    code.append(document.createTextNode(record.raw.slice(0, start)), node('mark', record.raw.slice(start, start + field.length)), document.createTextNode(record.raw.slice(start + field.length)));
                    description.textContent = `${field.name} · positions ${field.start}–${field.start + field.length - 1}: ${field.meaning}`;
                } else { code.textContent = record.raw; description.textContent = `Record length: ${record.raw.length}; expected 94 characters. Select a field.`; }
            }
            record.fields.forEach(field => {
                const button = node('button', field.name, 'btn btn-outline-secondary btn-sm'); button.type = 'button'; button.setAttribute('aria-pressed', 'false');
                button.addEventListener('click', () => { fields.querySelectorAll('button').forEach(b => b.setAttribute('aria-pressed', String(b === button))); highlight(field); }); fields.append(button);
            });
            inspector.append(raw, fields, description); highlight(null);
        }
        for (const record of records) {
            if (record.type === '5') {
                batch = node('details', null, 'nacha-tree-batch');
                const number = record.fields.find(f => f.name === 'Batch number')?.rawValue.trim();
                const sec = record.fields.find(f => f.name === 'SEC code')?.rawValue.trim();
                batch.append(node('summary', `Batch ${number || '?'} · ${sec || 'unknown SEC'}`)); file.append(batch); entry = null;
            }
            const row = node('div', null, 'nacha-tree-record');
            const button = node('button', `${record.number}. ${record.name}`, 'btn btn-outline-secondary btn-sm'); button.type = 'button'; button.setAttribute('aria-pressed', 'false');
            button.addEventListener('click', () => inspect(record, button)); buttons.push(button); row.append(button);
            if (record.type === '6') {
                entry = node('details', null, 'nacha-tree-entry');
                const amount = record.fields.find(f => f.name === 'Amount')?.rawValue;
                entry.append(node('summary', `Entry record ${record.number} · ${/^\d+$/.test(amount || '') ? money(Number(amount) / 100) : 'invalid amount'}`), row);
                (batch || file).append(entry);
            } else if (record.type === '7' && entry) entry.append(row);
            else {
                (record.type === '1' || record.type === '9' ? file : batch || file).append(row);
                if (record.type === '8' || record.type === '9') { batch = null; entry = null; }
            }
            if (record.name.endsWith('control')) {
                const totals = record.fields.filter(f => f.name.endsWith('total') || f.name.includes('count'));
                row.append(node('p', totals.map(f => f.name.endsWith('total') ? f.meaning : f.name + ': ' + f.rawValue.trim()).join(' · '), 'small text-secondary'));
            }
        }
        if (records.length) inspect(records[0], buttons[0]);
        document.querySelector('[name="Payload"]')?.addEventListener('input', () => root.querySelector('[data-nacha-stale]').hidden = false);
    });
})();
