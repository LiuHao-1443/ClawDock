import { describe, it, expect, beforeEach } from "vitest";
import { useUiStore } from "./ui-store";

describe("UiStore", () => {
  beforeEach(() => {
    useUiStore.setState({
      settingsOpen: false,
      consoleOpen: false,
      activeSettingsTab: "model",
    });
  });

  it("has correct defaults", () => {
    const state = useUiStore.getState();
    expect(state.settingsOpen).toBe(false);
    expect(state.consoleOpen).toBe(false);
    expect(state.activeSettingsTab).toBe("model");
  });

  it("toggleSettings flips settingsOpen", () => {
    useUiStore.getState().toggleSettings();
    expect(useUiStore.getState().settingsOpen).toBe(true);
    useUiStore.getState().toggleSettings();
    expect(useUiStore.getState().settingsOpen).toBe(false);
  });

  it("setSettingsOpen sets exact value", () => {
    useUiStore.getState().setSettingsOpen(true);
    expect(useUiStore.getState().settingsOpen).toBe(true);
    useUiStore.getState().setSettingsOpen(false);
    expect(useUiStore.getState().settingsOpen).toBe(false);
  });

  it("toggleConsole flips consoleOpen", () => {
    useUiStore.getState().toggleConsole();
    expect(useUiStore.getState().consoleOpen).toBe(true);
    useUiStore.getState().toggleConsole();
    expect(useUiStore.getState().consoleOpen).toBe(false);
  });

  it("setActiveSettingsTab switches tabs", () => {
    useUiStore.getState().setActiveSettingsTab("channel");
    expect(useUiStore.getState().activeSettingsTab).toBe("channel");
    useUiStore.getState().setActiveSettingsTab("model");
    expect(useUiStore.getState().activeSettingsTab).toBe("model");
  });
});
