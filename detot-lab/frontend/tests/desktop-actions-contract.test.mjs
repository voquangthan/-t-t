import assert from "node:assert/strict";
import { test } from "node:test";
import {
  githubAccountPresentation,
  pullRequestChecksSummary,
  pullRequestMetadata,
} from "../src/desktopActionsModel.ts";

test("Companion verified account payload renders the confirmed username", () => {
  const view = githubAccountPresentation({ status: "verified", username: "voquangthan" });
  assert.equal(view.status, "verified");
  assert.match(view.title, /Profile Edge đang đăng nhập/);
  assert.match(view.detail, /voquangthan/);
  assert.match(view.notice, /voquangthan/);
});

test("signed-out, failed, and unverified account payloads remain distinct", () => {
  const signedOut = githubAccountPresentation({ status: "signed_out", username: null });
  const failed = githubAccountPresentation({ status: "failed", username: null });
  const unverified = githubAccountPresentation({ status: "not_verified", username: null });
  assert.equal(signedOut.label, "Đang đăng xuất");
  assert.equal(failed.label, "Kiểm tra thất bại");
  assert.equal(unverified.label, "Chưa xác minh");
  assert.notEqual(signedOut.notice, failed.notice);
  assert.notEqual(failed.notice, unverified.notice);
  assert.equal(githubAccountPresentation({ status: "verified", username: null }).status, "not_verified");
});

test("Companion merged PR payload retains source and target branch labels", () => {
  const pr = {
    number: 3,
    title: "Computer Use",
    state: "closed",
    merged: true,
    headBranch: "codex/computer-use-vscode-fix",
    baseBranch: "codex/computer-use",
    headSha: "abcdef0123456789",
    checks: [],
  };
  assert.match(pullRequestMetadata(pr), /đã merge/);
  assert.match(pullRequestMetadata(pr), /codex\/computer-use-vscode-fix → codex\/computer-use/);
  assert.match(pullRequestMetadata(pr), /abcdef01/);
});

test("9+ Companion checks expose hidden failures, totals, and truncation", () => {
  const pr = {
    checks: Array.from({ length: 8 }, (_, index) => ({ name: `check-${index + 1}`, status: "completed", conclusion: "success" })),
    totalChecks: 9,
    displayedChecks: 8,
    checksTruncated: true,
    failedChecks: 1,
    failedCheckNames: ["check-9"],
  };
  const summary = pullRequestChecksSummary(pr);
  assert.match(summary, /8\/9 checks/);
  assert.match(summary, /1 check lỗi: check-9/);
  assert.match(summary, /danh sách bị cắt/);
});
