// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Database-backed form value pickers can fill one field or a correlated set of fields.
document.addEventListener("click", event => {
    const option = event.target.closest("[data-ach-values]");
    if (!option) return;

    const values = JSON.parse(option.dataset.achValues);
    Object.entries(values).forEach(([field, value]) => {
        const control = document.getElementById(field);
        if (!control) return;
        control.value = value;
        control.dispatchEvent(new Event("input", { bubbles: true }));
        control.dispatchEvent(new Event("change", { bubbles: true }));
    });

    const dropdown = option.closest(".dropdown")?.querySelector("[data-bs-toggle='dropdown']");
    if (dropdown && window.bootstrap) bootstrap.Dropdown.getOrCreateInstance(dropdown).hide();
});
