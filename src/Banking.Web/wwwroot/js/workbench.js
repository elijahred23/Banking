(() => {
    document.querySelectorAll("article").forEach(article => {
        const detail = article.querySelector(".topology-detail");
        if (!detail) return;
        article.querySelectorAll(".topology-node").forEach(node => node.addEventListener("click", () => {
            article.querySelectorAll(".topology-node").forEach(x => x.classList.remove("active"));
            node.classList.add("active");
            const time = new Date(node.dataset.time).toLocaleString();
            detail.textContent = `${node.dataset.stage} — owner: ${node.dataset.owner}. ${node.dataset.description} Recorded ${time}. The next stage is downstream evidence, not proof supplied by this stage.`;
        }));
    });
})();
