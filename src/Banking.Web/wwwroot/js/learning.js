(() => {
    const picker = document.getElementById("explorer-message");
    const output = document.getElementById("iso-learning-output");
    const comparePicker = document.getElementById("compare-message");
    const sandbox = document.getElementById("iso-sandbox");
    const sandboxResult = document.getElementById("sandbox-result");
    if (!picker || !output) return;

    const escapeHtml = value => value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
    const localName = node => node.localName || node.nodeName.split(":").pop();

    const render = () => {
        const source = document.getElementById(`iso-data-${picker.value}`);
        if (!source) {
            output.innerHTML = '<p class="text-secondary mb-0">Choose a message to begin.</p>';
            return;
        }
        const xml = source.textContent.trim();
        if (sandbox) sandbox.value = xml;
        const parsed = new DOMParser().parseFromString(xml, "application/xml");
        if (parsed.querySelector("parsererror")) {
            output.innerHTML = '<div class="alert alert-warning mb-0">This scenario intentionally produced malformed XML. Compare the raw payload with a valid message.</div>';
            return;
        }
        const leaves = Array.from(parsed.getElementsByTagName("*"))
            .filter(element => element.children.length === 0 && element.textContent.trim());
        let compareValues = new Map();
        const compareSource = comparePicker?.value ? document.getElementById(`iso-data-${comparePicker.value}`) : null;
        if (compareSource) {
            const compareXml = new DOMParser().parseFromString(compareSource.textContent.trim(), "application/xml");
            if (!compareXml.querySelector("parsererror")) Array.from(compareXml.getElementsByTagName("*")).filter(x => x.children.length === 0).forEach(x => compareValues.set(localName(x), x.textContent.trim()));
        }
        const rows = leaves.map(element => { const name = localName(element); const value = element.textContent.trim(); const before = compareValues.get(name); return `<tr><th><code>${escapeHtml(name)}</code></th><td>${escapeHtml(value)}</td><td>${before === undefined ? "—" : before === value ? "Same" : `Changed from ${escapeHtml(before)}`}</td></tr>`; }).join("");
        output.innerHTML = `<div class="iso-learning-summary"><strong>${leaves.length} populated fields</strong><span>Read identifiers first, then parties, amount, settlement, and status.</span></div><div class="table-responsive"><table class="table mb-0"><thead><tr><th>Element</th><th>Value</th><th>Comparison</th></tr></thead><tbody>${rows}</tbody></table></div>`;
    };
    picker.addEventListener("change", render);
    comparePicker?.addEventListener("change", render);
    document.getElementById("validate-sandbox")?.addEventListener("click", () => {
        const parsed = new DOMParser().parseFromString(sandbox?.value || "", "application/xml");
        const error = parsed.querySelector("parsererror");
        sandboxResult.textContent = error ? "Not well-formed XML. Structural validation would stop this payload." : "Well-formed XML. Profile and business-rule validation are still required.";
        sandboxResult.className = `alert ${error ? "alert-warning" : "alert-info"} mt-3`;
    });
})();
