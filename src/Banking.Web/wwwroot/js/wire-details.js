(() => {
    const region = document.getElementById("wire-live-region");
    if (!region) return;

    const content = document.getElementById("wire-details-content");
    const stateText = document.getElementById("live-state-text");
    const state = region.querySelector(".live-state");
    const wireId = region.dataset.wireId;
    let refreshInProgress = false;
    let refreshQueued = false;

    function setConnectionState(text, connected) {
        stateText.textContent = text;
        state.classList.toggle("live-connected", connected);
    }

    function escapeHtml(value) {
        return value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
    }

    function highlightXml(root = content) {
        root.querySelectorAll(".xml-viewer code").forEach(code => {
            const escaped = escapeHtml(code.textContent);
            code.innerHTML = escaped
                .replace(/(&lt;\/?)([\w:.-]+)/g, '$1<span class="xml-tag">$2</span>')
                .replace(/([\w:.-]+)=("[^"]*")/g, '<span class="xml-attr">$1</span>=<span class="xml-value">$2</span>');
        });
    }

    async function refresh() {
        if (refreshInProgress) {
            refreshQueued = true;
            return;
        }
        refreshInProgress = true;
        try {
            const response = await fetch(region.dataset.fragmentUrl, {
                headers: { "X-Requested-With": "XMLHttpRequest" },
                cache: "no-store"
            });
            if (!response.ok) throw new Error(`Wire refresh failed (${response.status})`);
            content.innerHTML = await response.text();
            wireMessageActions(content);
            highlightXml(content);
            setConnectionState(`Live · updated ${new Date().toLocaleTimeString()}`, true);
        } catch (error) {
            console.error(error);
            setConnectionState("Live update unavailable · retrying", false);
        } finally {
            refreshInProgress = false;
            if (refreshQueued) {
                refreshQueued = false;
                void refresh();
            }
        }
    }

    function wireMessageActions(root = content) {
        root.querySelectorAll(".iso-message").forEach(message => {
            const xml = () => message.querySelector(".xml-viewer code").textContent;
            message.querySelector(".copy-xml")?.addEventListener("click", async event => {
                await navigator.clipboard.writeText(xml());
                const button = event.currentTarget;
                const original = button.textContent;
                button.textContent = "Copied";
                setTimeout(() => button.textContent = original, 1200);
            });
            message.querySelector(".download-xml")?.addEventListener("click", () => {
                const blob = new Blob([xml()], { type: "application/xml;charset=utf-8" });
                const link = document.createElement("a");
                link.href = URL.createObjectURL(blob);
                link.download = `${message.dataset.messageType}-${message.dataset.messageId}.xml`;
                link.click();
                URL.revokeObjectURL(link.href);
            });
        });
    }

    wireMessageActions();
    highlightXml();

    if (!window.signalR) {
        setConnectionState("Live client unavailable · checking periodically", false);
        setInterval(refresh, 15000);
        return;
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl(`/hubs/wires?wireId=${encodeURIComponent(wireId)}`)
        .withAutomaticReconnect([0, 1000, 3000, 10000])
        .build();

    connection.on("wireUpdated", refresh);
    connection.onreconnecting(() => setConnectionState("Reconnecting to live updates…", false));
    connection.onreconnected(() => {
        setConnectionState("Live", true);
        void refresh();
    });
    connection.onclose(() => setConnectionState("Live update disconnected", false));

    async function start() {
        try {
            await connection.start();
            setConnectionState("Live", true);
        } catch (error) {
            console.error(error);
            setConnectionState("Live update unavailable · retrying", false);
            setTimeout(start, 5000);
        }
    }

    void start();
})();
