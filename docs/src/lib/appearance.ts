export const themeModes = ['system', 'light', 'dark'] as const;
export const themePalettes = ['runic', 'moss', 'fjord', 'ember'] as const;

export type ThemeMode = (typeof themeModes)[number];
export type ThemePalette = (typeof themePalettes)[number];

export type Appearance = {
  mode: ThemeMode;
  palette: ThemePalette;
};

export type AppearanceStorage = {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
};

export type AppearanceRoot = {
  classList: {
    toggle(name: string, force?: boolean): boolean;
  };
  dataset: Record<string, string | undefined>;
  style: {
    colorScheme: string;
  };
};

export type AppearanceMediaQuery = {
  matches: boolean;
  addEventListener(type: 'change', listener: () => void): void;
  removeEventListener(type: 'change', listener: () => void): void;
};

export const appearanceKeys = {
  mode: 'runic-docs.theme-mode',
  palette: 'runic-docs.theme-palette',
} as const;

export const defaultAppearance: Readonly<Appearance> = {
  mode: 'dark',
  palette: 'runic',
};

const colorSchemeQuery = '(prefers-color-scheme: dark)';

export function isThemeMode(value: unknown): value is ThemeMode {
  return themeModes.some((mode) => mode === value);
}

export function isThemePalette(value: unknown): value is ThemePalette {
  return themePalettes.some((palette) => palette === value);
}

export function resolveDarkMode(
  mode: ThemeMode,
  systemPrefersDark: boolean,
): boolean {
  return mode === 'dark' || (mode === 'system' && systemPrefersDark);
}

export function readAppearance(
  storage: AppearanceStorage | undefined = browserStorage(),
): Appearance {
  if (storage === undefined) return { ...defaultAppearance };

  try {
    const storedMode = storage.getItem(appearanceKeys.mode);
    const storedPalette = storage.getItem(appearanceKeys.palette);
    return {
      mode: isThemeMode(storedMode) ? storedMode : defaultAppearance.mode,
      palette: isThemePalette(storedPalette)
        ? storedPalette
        : defaultAppearance.palette,
    };
  } catch {
    return { ...defaultAppearance };
  }
}

export function applyAppearance(
  mode: ThemeMode,
  palette: ThemePalette,
  root: AppearanceRoot | undefined = documentRoot(),
  media: AppearanceMediaQuery | undefined = colorSchemeMedia(),
): void {
  if (root === undefined) return;

  const dark = resolveDarkMode(mode, media?.matches ?? false);
  root.classList.toggle('dark', dark);
  root.dataset.theme = palette;
  root.style.colorScheme = dark ? 'dark' : 'light';
}

export function saveAppearance(
  mode: ThemeMode,
  palette: ThemePalette,
  storage: AppearanceStorage | undefined = browserStorage(),
  root: AppearanceRoot | undefined = documentRoot(),
  media: AppearanceMediaQuery | undefined = colorSchemeMedia(),
): void {
  if (storage !== undefined) {
    try {
      storage.setItem(appearanceKeys.mode, mode);
      storage.setItem(appearanceKeys.palette, palette);
    } catch {
      // Applying the in-memory preference remains useful when storage is blocked.
    }
  }

  applyAppearance(mode, palette, root, media);
}

export function subscribeSystemAppearance(
  getAppearance: () => Appearance,
  media: AppearanceMediaQuery | undefined = colorSchemeMedia(),
  apply: (mode: ThemeMode, palette: ThemePalette) => void = applyAppearance,
): () => void {
  if (media === undefined) return () => {};

  const update = (): void => {
    const appearance = getAppearance();
    if (appearance.mode === 'system') {
      apply(appearance.mode, appearance.palette);
    }
  };

  media.addEventListener('change', update);
  return () => media.removeEventListener('change', update);
}

function browserStorage(): AppearanceStorage | undefined {
  return typeof localStorage === 'undefined' ? undefined : localStorage;
}

function documentRoot(): AppearanceRoot | undefined {
  return typeof document === 'undefined' ? undefined : document.documentElement;
}

function colorSchemeMedia(): AppearanceMediaQuery | undefined {
  return typeof matchMedia === 'undefined'
    ? undefined
    : matchMedia(colorSchemeQuery);
}
