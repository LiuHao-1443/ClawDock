import { create } from "zustand";

type SettingsTab = "model" | "channel";

interface UiStore {
  settingsOpen: boolean;
  consoleOpen: boolean;
  activeSettingsTab: SettingsTab;

  toggleSettings: () => void;
  setSettingsOpen: (open: boolean) => void;
  toggleConsole: () => void;
  setConsoleOpen: (open: boolean) => void;
  setActiveSettingsTab: (tab: SettingsTab) => void;
}

export const useUiStore = create<UiStore>((set) => ({
  settingsOpen: false,
  consoleOpen: false,
  activeSettingsTab: "model",

  toggleSettings: () => set((s) => ({ settingsOpen: !s.settingsOpen })),
  setSettingsOpen: (open) => set({ settingsOpen: open }),
  toggleConsole: () => set((s) => ({ consoleOpen: !s.consoleOpen })),
  setConsoleOpen: (open) => set({ consoleOpen: open }),
  setActiveSettingsTab: (tab) => set({ activeSettingsTab: tab }),
}));
