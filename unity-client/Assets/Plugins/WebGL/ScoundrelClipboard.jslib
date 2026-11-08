mergeInto(LibraryManager.library, {
  ScoundrelClipboard_Arm: function (textPtr) {
    try {
      var text = UTF8ToString(textPtr);
      var api = window.__scoundrelClipboard;
      if (!api) {
        api = {
          armedText: null,
          lastCopiedText: null,
          lastCopiedAt: 0,
          copySync: function (str) {
            if (!str) return false;
            var ok = false;
            var prevActive = document.activeElement;
            var ta = document.createElement("textarea");
            ta.value = str;
            ta.setAttribute("readonly", "");
            ta.style.position = "fixed";
            ta.style.top = "0";
            ta.style.left = "-9999px";
            ta.style.width = "1px";
            ta.style.height = "1px";
            ta.style.opacity = "0";
            ta.style.pointerEvents = "none";
            document.body.appendChild(ta);
            try {
              ta.focus({ preventScroll: true });
              ta.select();
              ta.setSelectionRange(0, str.length);
              ok = Boolean(document.execCommand("copy"));
            } catch (e) {
              ok = false;
            }
            document.body.removeChild(ta);
            if (prevActive && typeof prevActive.focus === "function") {
              try {
                prevActive.focus({ preventScroll: true });
              } catch (_) {}
            }
            if (ok) {
              api.lastCopiedText = str;
              api.lastCopiedAt = performance.now();
            }
            return ok;
          },
          onDomGesture: function () {
            if (!api.armedText) return;
            var str = api.armedText;
            if (api.lastCopiedText === str && (performance.now() - api.lastCopiedAt) < 250) {
              return;
            }
            var ok = api.copySync(str);
            if (!ok && navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
              navigator.clipboard.writeText(str).then(function () {
                api.lastCopiedText = str;
                api.lastCopiedAt = performance.now();
              }).catch(function () {});
            }
          }
        };
        window.addEventListener("mouseup", api.onDomGesture, true);
        window.addEventListener("touchend", api.onDomGesture, true);
        window.addEventListener("click", api.onDomGesture, true);
        window.__scoundrelClipboard = api;
      }
      api.armedText = text || null;
    } catch (err) {
      console.warn("[ScoundrelClipboard] Arm error:", err);
    }
  },

  ScoundrelClipboard_Disarm: function () {
    if (window.__scoundrelClipboard) {
      window.__scoundrelClipboard.armedText = null;
    }
  },

  ScoundrelClipboard_CopyNow: function (textPtr) {
    try {
      var text = UTF8ToString(textPtr);
      if (!text) return 0;

      var api = window.__scoundrelClipboard;
      if (api && api.lastCopiedText === text && (performance.now() - api.lastCopiedAt) < 750) {
        return 1;
      }

      var ok = false;
      if (api && typeof api.copySync === "function") {
        ok = api.copySync(text);
      } else {
        var prevActive = document.activeElement;
        var ta = document.createElement("textarea");
        ta.value = text;
        ta.setAttribute("readonly", "");
        ta.style.position = "fixed";
        ta.style.top = "0";
        ta.style.left = "-9999px";
        ta.style.width = "1px";
        ta.style.height = "1px";
        ta.style.opacity = "0";
        ta.style.pointerEvents = "none";
        document.body.appendChild(ta);
        try {
          ta.focus({ preventScroll: true });
          ta.select();
          ta.setSelectionRange(0, text.length);
          ok = Boolean(document.execCommand("copy"));
        } catch (e) {
          ok = false;
        }
        document.body.removeChild(ta);
        if (prevActive && typeof prevActive.focus === "function") {
          try {
            prevActive.focus({ preventScroll: true });
          } catch (_) {}
        }
      }

      if (ok) {
        return 1;
      }

      if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
        navigator.clipboard.writeText(text).catch(function () {
          try {
            window.prompt("Copy Solana Wallet Address:", text);
          } catch (_) {}
        });
        return 1;
      }

      try {
        window.prompt("Copy Solana Wallet Address:", text);
      } catch (_) {}
      return 0;
    } catch (err) {
      console.warn("[ScoundrelClipboard] CopyNow error:", err);
      return 0;
    }
  }
});
