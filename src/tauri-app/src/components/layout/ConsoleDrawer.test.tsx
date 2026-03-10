import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { ConsoleDrawer } from "./ConsoleDrawer";
import { useGatewayStore } from "../../stores/gateway-store";
import { useUiStore } from "../../stores/ui-store";

describe("ConsoleDrawer", () => {
  beforeEach(() => {
    useGatewayStore.setState({ logs: [] });
    useUiStore.setState({ consoleOpen: true });
  });

  it("shows empty state message when no logs", () => {
    render(<ConsoleDrawer />);
    expect(screen.getByText("No log output yet.")).toBeInTheDocument();
  });

  it("renders log messages", () => {
    useGatewayStore.setState({
      logs: [
        { id: 1, timestamp: Date.now(), message: "Gateway started" },
        { id: 2, timestamp: Date.now(), message: "Listening on :18789" },
      ],
    });
    render(<ConsoleDrawer />);
    expect(screen.getByText("Gateway started")).toBeInTheDocument();
    expect(screen.getByText("Listening on :18789")).toBeInTheDocument();
  });

  it("clears logs when Clear button is clicked", () => {
    useGatewayStore.setState({
      logs: [{ id: 1, timestamp: Date.now(), message: "test log" }],
    });
    render(<ConsoleDrawer />);
    fireEvent.click(screen.getByText("Clear"));
    expect(useGatewayStore.getState().logs).toEqual([]);
  });

  it("displays Console header text", () => {
    render(<ConsoleDrawer />);
    expect(screen.getByText("Console")).toBeInTheDocument();
  });
});
