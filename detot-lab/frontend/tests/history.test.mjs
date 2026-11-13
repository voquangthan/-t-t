import assert from "node:assert/strict";
import { test } from "node:test";
import { visibleConversations, forkMessages } from "../src/history.ts";

test("search includes message text and returns pinned conversations first", () => {
  const items = [
    { id: "1", title: "Trao đổi", pinned: false, messages: [{ text: "LỊCH thử" }] },
    { id: "2", title: "Lịch khác", pinned: true, messages: [] },
    { id: "3", title: "Ghi chú", pinned: true, messages: [] },
  ];
  assert.deepEqual(visibleConversations(items, " lịch ").map(item => item.id), ["2", "1"]);
  assert.equal(items[0].id, "1");
});

test("forking excludes the edited message and preserves the original", () => {
  const messages = [{ id: "a" }, { id: "b" }, { id: "c" }];
  assert.deepEqual(forkMessages(messages, 1), [{ id: "a" }]);
  assert.equal(messages.length, 3);
});
