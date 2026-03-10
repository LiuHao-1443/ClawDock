import { create } from "zustand";
import type { GatewayStatus, LogEntry } from "../lib/types";

const MAX_LOG_LINES = 500;
let logIdCounter = 0;

interface GatewayStore {
  status: GatewayStatus;
  logs: LogEntry[];
  dashboardUrl: string | null;
  errorMessage: string | null;

  setStatus: (status: GatewayStatus) => void;
  addLog: (message: string) => void;
  clearLogs: () => void;
  setDashboardUrl: (url: string | null) => void;
  setErrorMessage: (message: string | null) => void;
}

export const useGatewayStore = create<GatewayStore>((set) => ({
  status: "Stopped",
  logs: [],
  dashboardUrl: null,
  errorMessage: null,

  setStatus: (status) => set({ status }),

  addLog: (message) =>
    set((state) => {
      const newLogs = [
        ...state.logs,
        { id: ++logIdCounter, timestamp: Date.now(), message },
      ];
      if (newLogs.length > MAX_LOG_LINES) {
        return { logs: newLogs.slice(-MAX_LOG_LINES) };
      }
      return { logs: newLogs };
    }),

  clearLogs: () => set({ logs: [] }),
  setDashboardUrl: (url) => set({ dashboardUrl: url }),
  setErrorMessage: (message) => set({ errorMessage: message }),
}));
