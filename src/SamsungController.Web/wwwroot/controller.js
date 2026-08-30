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
    },
    scrollToElement: function (element) {
        if (!element) {
            return;
        }

        window.requestAnimationFrame(function () {
            element.scrollIntoView({ behavior: "smooth", block: "start" });
        });
    },
    bindAutoIndent: function (element) {
        if (!element || element.dataset.autoIndentBound === "true") {
            return;
        }

        element.dataset.autoIndentBound = "true";
        element.addEventListener("keydown", function (event) {
            if (event.key !== "Enter") {
                return;
            }

            const selectionStart = element.selectionStart;
            const selectionEnd = element.selectionEnd;
            const textBeforeCursor = element.value.slice(0, selectionStart);
            const currentLineStart = textBeforeCursor.lastIndexOf("\n") + 1;
            const currentLine = textBeforeCursor.slice(currentLineStart);
            const indentation = currentLine.match(/^[ \t]*/)?.[0] ?? "";

            event.preventDefault();
            element.setRangeText(`\n${indentation}`, selectionStart, selectionEnd, "end");
            element.dispatchEvent(new Event("input", { bubbles: true }));
        });
    },
    bindOutlineLineNumbers: function (element, lineNumbers) {
        if (!element || !lineNumbers) {
            return;
        }

        if (element.dataset.lineNumbersBound !== "true") {
            element.dataset.lineNumbersBound = "true";
            element.addEventListener("scroll", function () {
                lineNumbers.scrollTop = element.scrollTop;
            });
        }

        lineNumbers.scrollTop = element.scrollTop;
    },
    bindMenuTree: function (element) {
        if (!element || element.dataset.keyboardNavigationBound === "true") {
            return;
        }

        element.dataset.keyboardNavigationBound = "true";
        element.addEventListener("keydown", function (event) {
            if (!["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(event.key)) {
                return;
            }

            const items = Array.from(element.querySelectorAll(".menu-tree-node-select"));
            if (items.length === 0) {
                return;
            }

            const targetElement = event.target instanceof Element ? event.target : null;
            const currentRow = targetElement?.closest(".menu-tree-node");
            const current = currentRow?.querySelector(".menu-tree-node-select")
                ?? element.querySelector(".menu-tree-node.active .menu-tree-node-select");
            const currentIndex = items.indexOf(current);
            let destination = null;

            if (currentIndex < 0) {
                destination = event.key === "ArrowUp" ? items.at(-1) : items[0];
            } else if (event.key === "ArrowUp") {
                destination = items[Math.max(0, currentIndex - 1)];
            } else if (event.key === "ArrowDown") {
                destination = items[Math.min(items.length - 1, currentIndex + 1)];
            } else if (event.key === "ArrowLeft" && current.dataset.parentId) {
                destination = items.find(item => item.dataset.nodeId === current.dataset.parentId);
            } else if (event.key === "ArrowRight") {
                destination = items.find(item => item.dataset.parentId === current.dataset.nodeId);
            }

            event.preventDefault();
            event.stopPropagation();
            if (!destination) {
                return;
            }

            destination.focus({ preventScroll: true });
            destination.click();
            destination.scrollIntoView({ block: "nearest" });
        });
    }
};
