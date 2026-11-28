package io.honua.fixtureinput;
import android.app.Instrumentation;
import android.os.Bundle;
import android.view.accessibility.AccessibilityNodeInfo;
import java.nio.file.Files;
import java.nio.file.Paths;

public class FixtureInput extends Instrumentation {
  public void onCreate(Bundle args) { super.onCreate(args); start(); }
  public void onStart() {
    try {
      var ui = getUiAutomation();
      AccessibilityNodeInfo root = null;
      for (int attempt = 0; attempt < 60 && root == null; attempt++) {
        Thread.sleep(300);
        root = ui.getRootInActiveWindow();
      }
      var credentials = Files.readAllLines(Paths.get(getContext().getFilesDir().getPath(), "fixture"));
      var result = new Bundle();
      result.putString("root", root == null ? "none" : root.getPackageName().toString());
      result.putInt("inputCount", fill(root, credentials.get(0), credentials.get(1)));
      finish(-1, result);

    } catch (Exception e) {
      var result = new Bundle();
      result.putString("error", e.getClass().getSimpleName());
      finish(0, result);
    }
  }
  int fill(AccessibilityNodeInfo n, String user, String secret) {
    if (n == null) return 0;
    if ("android.widget.EditText".contentEquals(n.getClassName())) {
      var args = new Bundle();
      args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, n.isPassword() ? secret : user);
      return n.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args) ? 1 : 0;
    }
    int result = 0;
    for (int i = 0; i < n.getChildCount(); i++) result += fill(n.getChild(i),user,secret);
    return result;
  }
}
