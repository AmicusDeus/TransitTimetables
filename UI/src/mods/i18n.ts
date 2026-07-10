import { useLocalization } from "cs2/l10n";

// Panel localization: every user-visible string goes through t(key, fallback, vars). Ids are
// "TransitTimetables.ui.<key>". No ui.* keys are registered yet, so English fallbacks are always used;
// wiring translations later is additive (register the keys in LocaleSource from Translations).
export function useT() {
    const loc = useLocalization();
    return (key: string, fallback: string, vars?: Record<string, string | number>) => {
        let s = fallback;
        try {
            const r = loc && loc.translate("TransitTimetables.ui." + key, fallback);
            if (r) s = r;
        } catch { /* fall back to English */ }
        if (vars) {
            for (const k in vars) s = s.split("{" + k + "}").join(String(vars[k]));
        }
        return s;
    };
}
