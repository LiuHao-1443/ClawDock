import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { StatusBar } from "./StatusBar";
import { useGatewayStore } from "../../stores/gateway-store";

function renderWithProviders(ui: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
  );
}

describe("StatusBar", () => {
  beforeEach(() => {
    useGatewayStore.setState({ status: "Stopped" });
  });

  it("renders version text", () => {
    renderWithProviders(<StatusBar />);
    expect(screen.getByText(/ClawDock v/)).toBeInTheDocument();
  });

  it("renders gateway status", () => {
    renderWithProviders(<StatusBar />);
    expect(screen.getByText("Gateway: Stopped")).toBeInTheDocument();
  });

  it("reflects store status changes", () => {
    renderWithProviders(<StatusBar />);
    act(() => {
      useGatewayStore.setState({ status: "Running" });
    });
    expect(screen.getByText("Gateway: Running")).toBeInTheDocument();
  });
});
