import { describe, it, expect, beforeEach } from "vitest";
import { useGatewayStore } from "./gateway-store";

describe("GatewayStore", () => {
  beforeEach(() => {
    useGatewayStore.setState({
      status: "Stopped",
      logs: [],
      dashboardUrl: null,
      errorMessage: null,
    });
  });

  it("has correct default state", () => {
    const state = useGatewayStore.getState();
    expect(state.status).toBe("Stopped");
    expect(state.logs).toEqual([]);
    expect(state.dashboardUrl).toBeNull();
    expect(state.errorMessage).toBeNull();
  });

  it("setStatus updates status", () => {
    useGatewayStore.getState().setStatus("Running");
    expect(useGatewayStore.getState().status).toBe("Running");
  });

  it("addLog appends a log entry with timestamp", () => {
    useGatewayStore.getState().addLog("hello");
    const logs = useGatewayStore.getState().logs;
    expect(logs).toHaveLength(1);
    expect(logs[0].message).toBe("hello");
    expect(logs[0].timestamp).toBeGreaterThan(0);
  });

  it("addLog enforces 500-line ring buffer", () => {
    const store = useGatewayStore.getState();
    for (let i = 0; i < 510; i++) {
      store.addLog(`line-${i}`);
    }
    const logs = useGatewayStore.getState().logs;
    expect(logs).toHaveLength(500);
    expect(logs[0].message).toBe("line-10");
    expect(logs[499].message).toBe("line-509");
  });

  it("clearLogs resets logs", () => {
    useGatewayStore.getState().addLog("test");
    useGatewayStore.getState().clearLogs();
    expect(useGatewayStore.getState().logs).toEqual([]);
  });

  it("setDashboardUrl updates url", () => {
    useGatewayStore.getState().setDashboardUrl("http://localhost:18789");
    expect(useGatewayStore.getState().dashboardUrl).toBe(
      "http://localhost:18789"
    );
  });

  it("setErrorMessage updates error", () => {
    useGatewayStore.getState().setErrorMessage("Connection failed");
    expect(useGatewayStore.getState().errorMessage).toBe("Connection failed");

    useGatewayStore.getState().setErrorMessage(null);
    expect(useGatewayStore.getState().errorMessage).toBeNull();
  });
});
