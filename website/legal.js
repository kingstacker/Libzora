"use strict";

// Language switch for the standalone legal pages. Shares the homepage's
// stored preference so the two stay in the same language.
(function () {
  const STORAGE_KEY = "libzora-site-language";
  const titles = {
    zh: document.documentElement.dataset.titleZh,
    en: document.documentElement.dataset.titleEn,
  };

  function initialLanguage() {
    const requested = new URLSearchParams(window.location.search).get("lang");
    if (requested === "zh" || requested === "en") return requested;
    try {
      const stored = window.localStorage.getItem(STORAGE_KEY);
      if (stored === "zh" || stored === "en") return stored;
    } catch {
      // Local storage is optional.
    }
    return navigator.language && navigator.language.toLowerCase().startsWith("en") ? "en" : "zh";
  }

  function apply(language, persist) {
    const root = document.documentElement;
    root.lang = language === "en" ? "en" : "zh-CN";
    root.dataset.legalLang = language;
    if (titles[language]) document.title = titles[language];

    const toggle = document.querySelector("#language-toggle");
    if (toggle) {
      toggle.textContent = language === "en" ? "中文" : "EN";
      toggle.setAttribute("aria-label", language === "en" ? "Switch to Chinese" : "切换为英文");
    }
    document.querySelectorAll("a[data-keep-lang]").forEach((link) => {
      const url = new URL(link.getAttribute("href"), window.location.href);
      url.searchParams.set("lang", language);
      link.href = url.pathname.split("/").pop() + url.search + url.hash;
    });

    if (!persist) return;
    try {
      window.localStorage.setItem(STORAGE_KEY, language);
    } catch {
      // Local storage is optional.
    }
  }

  let current = initialLanguage();
  apply(current, false);

  const toggle = document.querySelector("#language-toggle");
  if (toggle) {
    toggle.hidden = false;
    toggle.addEventListener("click", () => {
      current = current === "en" ? "zh" : "en";
      apply(current, true);
    });
  }
})();
