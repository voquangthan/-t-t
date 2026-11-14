export interface Message {
  id: string;
  role: "user" | "assistant";
  text: string;
  tool?: string;
  calendarContext?: string;
}

export interface Conversation {
  id: string;
  title: string;
  pinned: boolean;
  gatewayLinked?: boolean;
  practice?: boolean;
  miniPractice?: boolean;
  messages: Message[];
}

export function visibleConversations(items: Conversation[], query: string) {
  const term = query.trim().toLocaleLowerCase("vi");
  return items.filter(item => item.title.toLocaleLowerCase("vi").includes(term) ||
    item.messages.some(message => message.text.toLocaleLowerCase("vi").includes(term)))
    .sort((a, b) => Number(b.pinned) - Number(a.pinned));
}

export function forkMessages(messages: Message[], index: number) {
  return messages.slice(0, index);
}
