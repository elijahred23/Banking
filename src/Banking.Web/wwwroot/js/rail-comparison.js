/* Fictional lifecycle illustrations; never submits a payment or reads bank data. */
(() => {
    'use strict';
    const stages = ['Submitted', 'Transport delivered', 'Business accepted', 'Settlement and availability', 'Return requested', 'Return outcome'];
    const profiles = [
        {
            name: 'Fedwire', format: 'ISO 20022 XML · pacs.008 instruction; pacs.002 status',
            timing: 'Same banking day in this example, while the processing window is open.',
            closed: 'Wait until the next eligible processing window; settlement is not immediate.',
            availability: 'Recipient funds become available after settlement and receiving-bank posting in this illustration.',
            finality: 'Final when settled across Federal Reserve master accounts. A later return is a separate transfer.',
            request: 'camt.056 requests return; the receiving bank must respond. The original settlement stands.',
            outcome: 'Receiving bank agrees in this scenario; a separate pacs.004 return restores the sender funds.',
            returned: true
        },
        {
            name: 'FedNow', format: 'ISO 20022 XML · pacs.008 instruction; receiver confirmation and pacs.002 status',
            timing: 'Seconds, 24/7, assuming both participants are available and the payment is eligible.',
            closed: 'Continues 24/7 in this example; participant availability and eligibility still matter.',
            availability: 'Recipient funds are immediately available following successful final settlement in this example.',
            finality: 'Immediate final settlement across Federal Reserve accounts; no unilateral cancellation after settlement.',
            request: 'camt.056 requests return. It cannot undo final settlement or guarantee recipient consent.',
            outcome: 'Recipient declines in this scenario. No return transfer occurs; sender remains debited.',
            returned: false
        },
        {
            name: 'ACH', format: 'NACHA fixed-width 94-character records · PPD credit in a batch/file; not ISO XML',
            timing: 'Next eligible batch and effective-date settlement in this example; not an instant payment.',
            closed: 'Wait for the next eligible banking-day batch and effective date. No weekend settlement is assumed.',
            availability: 'Recipient availability follows settlement and receiving-bank availability policy; this example makes funds available at posting.',
            finality: 'Settlement does not eliminate applicable return rights. Rules and deadlines depend on entry type and reason.',
            request: 'Originating bank requests return from the receiving bank; this is not an automatic reversal.',
            outcome: 'Receiving bank agrees in this scenario. A NACHA return entry and subsequent settlement restore sender funds.',
            returned: true
        },
        {
            name: 'SWIFT CBPR+', format: 'ISO 20022 XML · pacs.008.001.08 over simulated FINplus; serial USD correspondent route',
            timing: 'Illustrative next banking day after correspondent processing; actual timing depends on route and value date.',
            closed: 'Messaging can precede settlement; correspondent cutoffs and calendars delay funds movement.',
            availability: 'Recipient can use funds after correspondent funding and beneficiary-bank credit, not merely SWIFT delivery.',
            finality: 'Determined by the underlying settlement arrangement. SWIFT transports messages; it does not settle money.',
            request: 'camt.056 requests return through the correspondent route. Consent and funding remain separate.',
            outcome: 'Correspondents and beneficiary bank agree in this scenario; a separate pacs.004 return restores the USD principal. Fees and FX are excluded.',
            returned: true
        }
    ];
    const money = cents => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(cents / 100);
    function snapshot(profile, cents, stage) {
        const blocked = profile.name === 'FedNow' && cents > 1000000000;
        const settled = !blocked && stage >= 3;
        const returned = settled && stage === 5 && profile.returned;
        const held = !blocked && stage < 3 ? cents : 0;
        const debit = settled && !returned ? cents : 0;
        return { blocked, held, debit, returned, sender: 2000000000 - debit, available: 2000000000 - debit - held, recipient: debit };
    }
    // Export the presentation model for deterministic checks without a browser.
    if (typeof module !== 'undefined' && module.exports) module.exports = { profiles, stages, snapshot };
    if (typeof document === 'undefined') return;
    const form = document.getElementById('rail-experiment-form');
    if (!form) return;
    const run = document.getElementById('rail-run');
    const next = document.getElementById('rail-next');
    const results = document.getElementById('rail-results');
    let stage = 0, cents = 12500, closed = false;
    function field(list, label, value) {
        const term = document.createElement('dt'); term.textContent = label;
        const description = document.createElement('dd'); description.textContent = value;
        list.append(term, description);
    }
    function render() {
        results.replaceChildren();
        document.getElementById('rail-stage').textContent = `Stage ${stage + 1} of ${stages.length}: ${stages[stage]}`;
        document.getElementById('rail-run-summary').textContent = `Four independent ${money(cents)} USD credit examples for Alex Morgan · ${closed ? 'Weekend / after cutoff' : 'Banking day, windows open'}. Inputs are captured when you send; send again to apply changes.`;
        for (const profile of profiles) {
            const state = snapshot(profile, cents, stage);
            const card = document.createElement('article'); card.className = 'rail-experiment-card';
            const heading = document.createElement('h3'); heading.className = 'h5'; heading.textContent = profile.name;
            const status = document.createElement('p'); status.className = 'fw-bold';
            status.textContent = state.blocked ? 'Ineligible: exceeds the lab’s $10 million customer-transfer limit' : stage === 5 ? (state.returned ? 'Separate return settled' : 'Return declined; original payment settled') : stages[stage];
            const list = document.createElement('dl');
            field(list, 'Timing', state.blocked ? 'Not submitted. Reduce the amount and send again to compare all four rails.' : closed ? profile.closed : profile.timing);
            field(list, 'Message format', profile.format);
            field(list, 'Funds availability', profile.availability);
            field(list, 'Finality', profile.finality);
            field(list, 'Return handling', profile.request);
            if (!state.blocked && stage >= 4) field(list, 'Return evidence', stage === 4 ? 'Request only; no balance movement.' : profile.outcome);
            field(list, 'Sender ledger / held / available', `${money(state.sender)} / ${money(state.held)} / ${money(state.available)}`);
            field(list, 'Recipient available', money(state.recipient));
            field(list, 'Accounting evidence', state.blocked ? 'No hold or posting.' : stage < 3 ? 'Illustrative sender reservation only; no posted debit. Delivery and acceptance do not post settlement.' : state.returned ? 'Original settlement plus a separate balanced return journal; net principal movement is zero.' : 'Customer and settlement journals balance; sender principal debited and recipient credited.');
            card.append(heading, status, list); results.append(card);
        }
        next.disabled = stage === stages.length - 1;
        next.textContent = next.disabled ? 'Comparison complete' : `Advance to ${stages[stage + 1].toLowerCase()}`;
    }
    form.addEventListener('submit', event => {
        event.preventDefault();
        const input = document.getElementById('rail-amount');
        input.setCustomValidity('');
        const amount = Number(input.value), rounded = Math.round(amount * 100);
        if (!Number.isFinite(amount) || rounded < 1 || rounded > 2000000000 || Math.abs(amount * 100 - rounded) > 0.00001) {
            input.setCustomValidity('Enter an amount from $0.01 to $20,000,000 with at most two decimal places.'); input.reportValidity(); return;
        }
        cents = rounded; closed = document.getElementById('rail-window').value === 'closed'; stage = 0;
        run.hidden = false; render(); next.focus();
    });
    document.getElementById('rail-amount').addEventListener('input', event => event.target.setCustomValidity(''));
    next.addEventListener('click', () => { if (stage < stages.length - 1) { stage++; render(); } });
    document.getElementById('rail-reset').addEventListener('click', () => { stage = 0; run.hidden = true; results.replaceChildren(); document.getElementById('rail-amount').focus(); });
})();
