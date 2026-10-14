// Minimal JS-Interop Helper.
// Wird von Blazor via IJSRuntime aufgerufen.
window.slideGeneratorInterop = {
    copyText: async function (text) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text ?? "");
                return true;
            }
        } catch (e) {
            // Fallback unten
        }

        // Fallback fuer aeltere Browser oder nicht-sicheren Kontext
        try {
            const ta = document.createElement("textarea");
            ta.value = text ?? "";
            ta.setAttribute("readonly", "");
            ta.style.position = "absolute";
            ta.style.left = "-9999px";
            document.body.appendChild(ta);
            ta.select();
            const ok = document.execCommand("copy");
            document.body.removeChild(ta);
            return ok;
        } catch (e) {
            return false;
        }
    },

    downloadUrl: async function (url, filename) {
        const response = await fetch(url, { credentials: "same-origin" });
        if (!response.ok) {
            const text = await response.text();
            throw new Error(text || response.statusText);
        }
        const blob = await response.blob();
        const objectUrl = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = objectUrl;
        a.download = filename || "slide.pptx";
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(objectUrl);
    }
};
