import "@testing-library/jest-dom/vitest";
import { vi } from "vitest";

// Polyfill scrollIntoView for jsdom
Element.prototype.scrollIntoView = vi.fn();

// Mock @tauri-apps/api/core (invoke)
vi.mock("@tauri-apps/api/core", () => ({
  invoke: vi.fn(),
}));

// Mock @tauri-apps/api/event (listen/emit)
vi.mock("@tauri-apps/api/event", () => ({
  listen: vi.fn(() => Promise.resolve(() => {})),
  emit: vi.fn(),
}));

// Mock @tauri-apps/plugin-os
vi.mock("@tauri-apps/plugin-os", () => ({
  platform: vi.fn(() => Promise.resolve("windows")),
}));

// Mock @tauri-apps/plugin-shell
vi.mock("@tauri-apps/plugin-shell", () => ({
  open: vi.fn(),
}));

// Mock @tauri-apps/api/webviewWindow
vi.mock("@tauri-apps/api/webviewWindow", () => ({
  getCurrentWebviewWindow: vi.fn(() => ({
    onCloseRequested: vi.fn(() => Promise.resolve(() => {})),
    hide: vi.fn(),
  })),
}));
