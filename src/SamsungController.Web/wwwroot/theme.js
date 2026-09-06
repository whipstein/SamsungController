(function () {
    "use strict";

    const storageKey = "samsungController.theme";
    const root = document.documentElement;

    function normalize(value) {
        return value === "light" || value === "dark" ? value : null;
    }

    function readSavedTheme() {
        try {
            return normalize(window.localStorage.getItem(storageKey));
        } catch {
            return null;
        }
    }

    function preferredTheme() {
        return window.matchMedia?.("(prefers-color-scheme: light)").matches
            ? "light"
            : "dark";
    }

    function applyTheme(value, persist) {
        const theme = normalize(value) ?? "dark";
        root.dataset.theme = theme;
        root.style.colorScheme = theme;

        const themeColor = document.querySelector('meta[name="theme-color"]');
        if (themeColor) {
            themeColor.setAttribute("content", theme === "light" ? "#f3f7f8" : "#071018");
        }

        if (persist) {
            try {
                window.localStorage.setItem(storageKey, theme);
            } catch {
                // Private browsing may deny storage; the active page still changes theme.
            }
        }

        return theme;
    }

    applyTheme(readSavedTheme() ?? preferredTheme(), false);

    window.samsungTheme = {
        get: function () {
            return normalize(root.dataset.theme) ?? "dark";
        },
        set: function (value) {
            return applyTheme(value, true);
        }
    };
})();
