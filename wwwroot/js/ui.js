window.uiHelpers = {
    blurActiveElement: () => document.activeElement && document.activeElement.blur(),
    getItem: (key) => {
        try { return localStorage.getItem(key); } catch { return null; }
    },
    setItem: (key, value) => {
        try { localStorage.setItem(key, value); } catch { /* ignore */ }
    }
};
