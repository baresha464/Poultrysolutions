// Thin JS-interop layer over ApexCharts + CountUp.js for the Blazor chart wrapper components.
// Chart option objects are built in C# (as anonymous/typed objects) and serialized to JSON —
// since JSON has no function type, any option that needs a JS callback (axis/tooltip
// formatters) is embedded as its literal source text (e.g. "function(v){ return v + 'g'; }")
// and revived into a real function here before ApexCharts sees it.

window.chartHelpers = (function () {
    const instances = {};

    function reviveFunctions(node) {
        if (Array.isArray(node)) return node.map(reviveFunctions);
        if (node && typeof node === "object") {
            const out = {};
            for (const key in node) out[key] = reviveFunctions(node[key]);
            return out;
        }
        if (typeof node === "string" && /^\s*function\s*\(/.test(node)) {
            try { return new Function("return (" + node + ")")(); } catch { return node; }
        }
        return node;
    }

    function parse(optionsJson) {
        return reviveFunctions(JSON.parse(optionsJson));
    }

    return {
        render(elementId, optionsJson) {
            const el = document.getElementById(elementId);
            if (!el || typeof ApexCharts === "undefined") return;
            if (instances[elementId]) { instances[elementId].destroy(); delete instances[elementId]; }
            const chart = new ApexCharts(el, parse(optionsJson));
            instances[elementId] = chart;
            chart.render();
        },

        update(elementId, optionsJson) {
            if (!instances[elementId]) { this.render(elementId, optionsJson); return; }
            instances[elementId].updateOptions(parse(optionsJson), true, true);
        },

        destroy(elementId) {
            if (instances[elementId]) { instances[elementId].destroy(); delete instances[elementId]; }
        },

        countUp(elementId, value, decimals, prefix, suffix) {
            const el = document.getElementById(elementId);
            if (!el) return;
            if (typeof countUp === "undefined") { el.textContent = (prefix || "") + value + (suffix || ""); return; }
            const counter = new countUp.CountUp(elementId, value, {
                decimalPlaces: decimals || 0,
                duration: 1,
                separator: ",",
                prefix: prefix || "",
                suffix: suffix || "",
            });
            if (!counter.error) counter.start();
            else el.textContent = (prefix || "") + value + (suffix || "");
        },
    };
})();
