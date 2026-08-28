import { useState } from "react";

export function ConfirmModal({
  open,
  title,
  message,
  confirmLabel = "Confirm",
  danger = false,
  showCheckoutOption = false,
  onConfirm,
  onClose,
}: {
  open: boolean;
  title: string;
  message: string;
  confirmLabel?: string;
  danger?: boolean;
  showCheckoutOption?: boolean;
  onConfirm: (deleteCheckout: boolean) => void;
  onClose: () => void;
}) {
  const [deleteCheckout, setDeleteCheckout] = useState(false);
  if (!open) return null;
  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginBottom: 8 }}>{title}</h3>
        <p style={{ color: "var(--text-muted)", marginBottom: 16 }}>{message}</p>
        {showCheckoutOption && (
          <label style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 16, fontSize: 14 }}>
            <input type="checkbox" checked={deleteCheckout} onChange={(e) => setDeleteCheckout(e.target.checked)} style={{ width: "auto" }} />
            Also delete Git checkout (rm -rf local_path)
          </label>
        )}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <button onClick={onClose}>Cancel</button>
          <button className={danger ? "danger" : "primary"} onClick={() => onConfirm(deleteCheckout)}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
