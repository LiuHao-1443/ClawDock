import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { SettingsPanel } from "./SettingsPanel";
import { useUiStore } from "../../stores/ui-store";

function renderWithProviders(ui: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
  );
}

describe("SettingsPanel", () => {
  beforeEach(() => {
    useUiStore.setState({
      settingsOpen: true,
      activeSettingsTab: "model",
    });
  });

  it("renders header with title", () => {
    renderWithProviders(<SettingsPanel />);
    expect(screen.getByText("Settings")).toBeInTheDocument();
  });

  it("renders model and channel tabs", () => {
    renderWithProviders(<SettingsPanel />);
    expect(screen.getByText("Model Config")).toBeInTheDocument();
    expect(screen.getByText("Channel Config")).toBeInTheDocument();
  });

  it("switches to channel tab on click", () => {
    renderWithProviders(<SettingsPanel />);
    fireEvent.click(screen.getByText("Channel Config"));
    expect(useUiStore.getState().activeSettingsTab).toBe("channel");
  });
});
