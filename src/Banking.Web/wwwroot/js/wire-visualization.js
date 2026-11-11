(() => {
    const money = value => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
    function el(tag, text, className) {
        const node = document.createElement(tag);
        if (text != null) node.textContent = text;
        if (className) node.className = className;
        return node;
    }
    function button(text, action) {
        const node = el('button', text, 'btn btn-outline-secondary btn-sm');
        node.type = 'button'; node.addEventListener('click', action); return node;
    }
    function mount(root, data, form, messages) {
        const panel = el('section', null, 'payment-visualization');
        panel.append(el('h3', 'Projected payment journey', 'h5'), el('p',
            'Snapshot forecast for this instruction only. Approval, compliance, routing, rail availability, and concurrent activity may change the result. Nothing is reserved or posted. Outcome selection changes this projection only; the XML below remains the instruction generated from your form.'));
        const controls = el('div', null, 'visual-controls');
        const label = el('label', 'Explore an outcome');
        const select = el('select', null, 'form-select');
        for (const branch of data.branches) {
            const option = el('option', branch.name); option.value = branch.id; select.append(option);
        }
        select.value = data.selectedBranch; label.append(select);
        const sliderLabel = el('label', 'Journey stage');
        const slider = el('input'); slider.type = 'range'; slider.min = '0'; slider.value = '0'; slider.step = '1';
        sliderLabel.append(slider);
        controls.append(label, sliderLabel); panel.append(controls);
        const route = el('ol', null, 'visual-route'); route.setAttribute('aria-label', 'Conceptual payment path');
        const routeNames = [data.sender, data.correspondent ? 'Simulated correspondent path' : data.rail + ' simulator', data.receiver];
        routeNames.forEach(name => route.append(el('li', name)));
        const timeline = el('div', null, 'visual-stages'); timeline.setAttribute('role', 'group'); timeline.setAttribute('aria-label', 'Select journey stage');
        const status = el('div'); status.setAttribute('aria-live', 'polite');
        const balances = el('div', null, 'visual-balances');
        const journal = el('div', null, 'visual-journal');
        panel.append(route, timeline, status, balances, journal);
        let index = 0;
        function render() {
            const branch = data.branches.find(item => item.id === select.value);
            index = Math.min(index, branch.steps.length - 1);
            slider.max = String(branch.steps.length - 1); slider.value = String(index);
            const step = branch.steps[index];
            slider.setAttribute('aria-valuetext', step.title);
            const restoreStageFocus = timeline.contains(document.activeElement);
            timeline.replaceChildren();
            branch.steps.forEach((item, i) => {
                const itemButton = button(`${i + 1}. ${item.title}`, () => { index = i; render(); });
                itemButton.setAttribute('aria-pressed', String(i === index)); timeline.append(itemButton);
            });
            if (restoreStageFocus) timeline.children[index]?.focus();
            [...route.children].forEach((node, i) => node.classList.toggle('is-active', i === (step.posted ? 2 : index >= 2 ? 1 : 0)));
            status.replaceChildren(el('h4', step.title, 'h6'), el('p', step.detail), el('p', 'Message / evidence: ' + step.boundary, 'visual-boundary'));
            balances.replaceChildren();
            for (const balance of step.balances) {
                const card = el('div', null, 'visual-balance');
                card.append(el('strong', balance.name), el('span', `${money(balance.before)} → ${money(balance.after)}`),
                    el('small', `Change: ${money(balance.after - balance.before)}`)); balances.append(card);
            }
            journal.replaceChildren(el('h4', 'Projected originating-bank accounting', 'h6'));
            if (!step.posted) journal.append(el('p', 'No posted journal at this stage. A reservation changes availability, not the ledger.'));
            else {
                const accounts = data.institution
                    ? ['Outgoing institution transfer clearing', 'Bank settlement cash']
                    : ['Customer deposit liability', data.correspondent ? 'Simulated correspondent position' : 'Fed master account'];
                const entries = el('div', null, 'visual-taccounts');
                accounts.forEach((name, i) => {
                    const account = el('div', null, 'visual-taccount');
                    account.append(el('strong', name));
                    const sides = el('div'); sides.append(el('span', 'Debit\n' + (i === 0 ? money(data.amount) : '—')),
                        el('span', 'Credit\n' + (i === 1 ? money(data.amount) : '—'))); account.append(sides); entries.append(account);
                });
                journal.append(entries, el('p', `Balanced: debits ${money(data.amount)} = credits ${money(data.amount)}. Illustrative account labels; inspect recorded journals for actual account codes.`, 'small'));
            }
        }
        select.addEventListener('change', () => { index = 0; render(); });
        slider.addEventListener('input', () => { index = Number(slider.value); render(); });
        render();
        const duplicate = el('details', null, 'mt-3'); duplicate.append(el('summary', 'Try duplicate delivery'));
        duplicate.append(el('p', 'Fictional accepted-payment experiment: both deliveries carry the same correlation key. This does not send a message or exercise a live consumer.'));
        const deliveryStatus = el('p'); deliveryStatus.setAttribute('role', 'status');
        const attempts = el('div', null, 'visual-duplicates'); let count = 0;
        const draw = () => { deliveryStatus.textContent = `${count} deliveries → ${count ? 1 : 0} business action → ${count ? 1 : 0} balanced journal`; };
        duplicate.append(button('Deliver same message', () => {
            count++; attempts.append(el('span', `Delivery ${count}: DEMO-SAME-KEY → ${count === 1 ? 'processed' : 'duplicate ignored'}`)); draw();
        }), button('Reset experiment', () => { count = 0; attempts.replaceChildren(); draw(); }), attempts, deliveryStatus); draw(); panel.append(duplicate);
        root.prepend(panel);
        traceFields(root, form, messages);
    }
    function traceFields(root, form, messages) {
        const specs = [
            ['Amount', 'Amount', ['IntrBkSttlmAmt']],
            ['Beneficiary', 'ReceiverName', ['Cdtr', 'Nm']],
            ['Receiving account', 'BeneficiaryAccountNumber', ['CdtrAcct', 'Id']],
            ['Reference', 'CustomerReference', ['PmtId', 'EndToEndId']],
            ['Receiving bank', 'ReceiverBankId', ['CdtrAgt']]
        ];
        const group = el('section', null, 'visual-field-trace');
        group.append(el('h3', 'Follow form fields into XML', 'h5'), el('p', 'Select a field or a marked XML value. Mapping uses element paths; repeated values are kept distinct.'));
        const buttons = el('div', null, 'visual-controls');
        const status = el('p'); status.setAttribute('role', 'status');
        const fieldButtons = new Map();
        function choose(key, scroll) {
            root.querySelectorAll('[data-xml-field]').forEach(node => node.classList.toggle('is-traced', node.dataset.xmlField === key));
            fieldButtons.forEach((node, field) => node.setAttribute('aria-pressed', String(field === key)));
            const spec = specs.find(item => item[0] === key);
            status.textContent = `${key}: ${spec[2].join(' / ')}. Source form: ${spec[1]}.`;
            if (scroll) root.querySelector('[data-xml-field="' + key + '"]')?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
            else fieldButtons.get(key)?.focus();
        }
        const codes = root.querySelectorAll('.xml-viewer code');
        let valid = false;
        codes.forEach((code, messageIndex) => {
            const xml = messages[messageIndex].xml;
            const parsed = new DOMParser().parseFromString(xml, 'application/xml');
            if (parsed.querySelector('parsererror')) return;
            valid = true; code.replaceChildren(); const stack = [];
            const tokens = xml.match(/<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<[^>]*>|[^<]+/g) || [];
            for (const token of tokens) {
                const closing = /^<\//.test(token);
                const start = token.match(/^<(?![!?/])([\w:.-]+)/);
                if (start) stack.push(start[1].split(':').pop());
                const match = specs.find(spec => {
                    const path = spec[2];
                    return path.length === 1 ? stack.includes(path[0]) : stack.includes(path[0]) && stack.at(-1) === path[1];
                });
                const span = el('span', token, token.startsWith('<') ? 'xml-tag' : '');
                if (match) {
                    span.dataset.xmlField = match[0];
                    // Only values are focusable to keep keyboard traversal compact.
                    if (!token.startsWith('<') && token.trim()) {
                        span.tabIndex = 0; span.setAttribute('role', 'button'); span.setAttribute('aria-label', 'Trace ' + match[0]);
                        span.addEventListener('click', () => choose(match[0], false));
                        span.addEventListener('keydown', event => {
                            if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); choose(match[0], false); }
                        });
                    }
                }
                code.append(span);
                if (closing || (start && /\/>$/.test(token))) stack.pop();
            }
        });
        for (const spec of specs) {
            if (!root.querySelector('[data-xml-field="' + spec[0] + '"]')) continue;
            const input = form.elements.namedItem(spec[1]);
            const value = input?.selectedOptions ? input.selectedOptions[0]?.textContent : input?.value;
            const control = button(spec[0] + (value ? ': ' + value : ''), () => choose(spec[0], true));
            control.setAttribute('aria-pressed', 'false'); buttons.append(control); fieldButtons.set(spec[0], control);
        }
        status.textContent = valid ? 'Choose a field to highlight its actual message elements.' : 'Malformed XML: repair the message before tracing element paths.';
        group.append(buttons, status); root.insertBefore(group, root.querySelector('.d-flex'));
    }
    window.bankingWireVisualization = { mount };
})();
