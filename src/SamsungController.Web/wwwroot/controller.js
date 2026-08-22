window.samsungController = {
    copyText: async function (value) {
        await navigator.clipboard.writeText(value);
    },
    downloadText: function (fileName, value) {
        const blob = new Blob([value], { type: "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    }
};
