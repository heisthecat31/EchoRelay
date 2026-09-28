namespace EchoRelay.App.Utils
{
    public static class ControlUtils
    {
        /// <summary>
        /// Runs a UI update on the control's thread. From another thread it's posted, not waited for: the server's threads
        /// call this for every event, and waiting on a busy window stalled the whole server.
        /// </summary>
        public static void InvokeUIThread(this Control control, Action method)
        {
            if (control.Disposing || control.IsDisposed) return;
            if (control.InvokeRequired)
                try
                {
                    control.BeginInvoke(method);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { } // no window handle (closing)
            else
                method();
        }
    }
}
