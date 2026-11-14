import AppKit
import SwiftUI

protocol PanelActions: AnyObject {
    func select(_ item: SizeModel.Item, toggle: Bool)
    func open(_ item: SizeModel.Item)
    func openFullDiskAccess()
}

struct PanelView: View {
    static let headerHeight: CGFloat = 60
    static let rowHeight: CGFloat = 22
    static let footerHeight: CGFloat = 26

    static func preferredHeight(rows: Int) -> CGFloat {
        headerHeight + 1 + 8 + CGFloat(max(rows, 1)) * (rowHeight + 1) + 1 + footerHeight
    }

    @ObservedObject var model: SizeModel
    let actions: PanelActions

    var body: some View {
        VStack(spacing: 0) {
            header.frame(height: Self.headerHeight)
            Divider()
            ScrollView(.vertical) {
                LazyVStack(spacing: 1) {
                    ForEach(model.items) { item in
                        RowView(
                            item: item,
                            total: model.total,
                            selected: model.selection.contains(item.id),
                            icon: model.icon(for: item),
                            actions: actions
                        )
                    }
                }
                .padding(4)
            }
            Divider()
            footer.frame(height: Self.footerHeight)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(VisualEffectBackground())
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(Color.black.opacity(0.18), lineWidth: 0.5)
        )
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 4) {
                Text(model.folderName)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: 4)
                if model.hasPartial {
                    Button(action: actions.openFullDiskAccess) {
                        Image(systemName: "lock.fill").font(.system(size: 10))
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(.orange)
                    .help("No Full Disk Access — some folders were not fully counted")
                }
                if model.isComputing {
                    ProgressView().controlSize(.mini)
                }
            }
            HStack(alignment: .firstTextBaseline, spacing: 3) {
                if model.isComputing || model.hasPartial {
                    Text("≥").font(.system(size: 13, weight: .semibold)).foregroundStyle(.secondary)
                }
                Text(Format.bytes(model.total))
                    .font(.system(size: 18, weight: .bold))
                    .monospacedDigit()
            }
            ShareBar(items: model.items, total: model.total)
                .frame(height: 4)
                .padding(.top, 3)
        }
        .padding(.horizontal, 12)
    }

    private var footer: some View {
        let selected = model.selectedItems
        return HStack {
            if selected.isEmpty {
                Text("Select items to see their total").foregroundStyle(.secondary)
            } else {
                Text("Selected: \(selected.count)")
                Spacer()
                Text(Format.bytes(selected.reduce(0) { $0 + $1.bytes }))
                    .fontWeight(.bold)
                    .monospacedDigit()
            }
        }
        .font(.system(size: 11))
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Share bar: the largest items are drawn more saturated.
struct ShareBar: View {
    private static let opacities: [Double] = [1, 0.78, 0.6, 0.46, 0.34, 0.24]

    let items: [SizeModel.Item]
    let total: Int64

    var body: some View {
        GeometryReader { geo in
            HStack(spacing: 1) {
                ForEach(Array(items.prefix(16).enumerated()), id: \.element.id) { i, item in
                    Rectangle()
                        .fill(Color.accentColor.opacity(i < Self.opacities.count ? Self.opacities[i] : 0.14))
                        .frame(width: total > 0 ? max(0, geo.size.width * CGFloat(item.bytes) / CGFloat(total) - 1) : 0)
                }
                Spacer(minLength: 0)
            }
        }
        .background(Color.secondary.opacity(0.15))
        .clipShape(Capsule())
    }
}

struct RowView: View {
    let item: SizeModel.Item
    let total: Int64
    let selected: Bool
    let icon: NSImage
    let actions: PanelActions

    private var fraction: CGFloat {
        total > 0 ? CGFloat(item.bytes) / CGFloat(total) : 0
    }

    private var barColor: Color {
        if item.computing { return Color.orange.opacity(0.22) }
        return Color.accentColor.opacity(selected ? 0.3 : 0.16)
    }

    var body: some View {
        HStack(spacing: 6) {
            Image(nsImage: icon)
                .resizable()
                .frame(width: 14, height: 14)
            Text(item.name)
                .font(.system(size: 11.5))
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 4)
            if item.partial {
                Image(systemName: "lock.fill")
                    .font(.system(size: 8))
                    .foregroundStyle(.orange)
            }
            Text((item.computing || item.partial ? "≥ " : "") + Format.bytes(item.bytes))
                .font(.system(size: 11, weight: .semibold))
                .monospacedDigit()
                .foregroundStyle(item.computing ? .secondary : .primary)
        }
        .padding(.horizontal, 6)
        .frame(height: PanelView.rowHeight)
        .background(alignment: .leading) {
            GeometryReader { geo in
                Rectangle()
                    .fill(barColor)
                    .frame(width: geo.size.width * fraction)
            }
        }
        .background(selected ? Color.accentColor.opacity(0.15) : Color.clear)
        .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { actions.open(item) }
        .simultaneousGesture(TapGesture().onEnded {
            actions.select(item, toggle: NSEvent.modifierFlags.contains(.command))
        })
        .help(item.isFolder ? LocalizedStringKey("Double-click to open in this window") : LocalizedStringKey("Double-click to open"))
    }
}

struct VisualEffectBackground: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .popover
        view.blendingMode = .behindWindow
        view.state = .active
        return view
    }

    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}
