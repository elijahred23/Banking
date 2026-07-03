(() => {
    const picker = document.getElementById("explorer-message");
    const output = document.getElementById("iso-learning-output");
    if (!picker || !output) return;

    const escapeHtml = value => value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
    const localName = node => node.localName || node.nodeName.split(":").pop();

    picker.addEventListener("change", () => {
        const source = document.getElementById(`iso-data-${picker.value}`);
        if (!source) {
            output.innerHTML = '<p class="text-secondary mb-0">Choose a message to begin.</p>';
            return;
        }
        const xml = source.textContent.trim();
        const parsed = new DOMParser().parseFromString(xml, "application/xml");
        if (parsed.querySelector("parsererror")) {
            output.innerHTML = '<div class="alert alert-warning mb-0">This scenario intentionally produced malformed XML. Compare the raw payload with a valid message.</div>';
            return;
        }
        const leaves = Array.from(parsed.getElementsByTagName("*"))
            .filter(element => element.children.length === 0 && element.textContent.trim());
        const rows = leaves.map(element => `<tr><th><code>${escapeHtml(localName(element))}</code></th><td>${escapeHtml(element.textContent.trim())}</td></tr>`).join("");
        output.innerHTML = `<div class="iso-learning-summary"><strong>${leaves.length} populated fields</strong><span>Read identifiers first, then parties, amount, settlement, and status.</span></div><div class="table-responsive"><table class="table mb-0"><thead><tr><th>Element</th><th>Value</th></tr></thead><tbody>${rows}</tbody></table></div>`;
    });
})();
