import CoreGraphics
import Foundation

enum NativeSwipeReleaseOutcome: Equatable {
    case closed
    case open
    case delete
}

/// Geometry and gesture thresholds for swipe-to-delete material cards.
enum NativeSwipeDeletePolicy {
    static let actionWidth: CGFloat = 82

    static func revealThreshold(actionWidth: CGFloat = actionWidth) -> CGFloat {
        actionWidth * 0.45
    }

    static func fullSwipeThreshold(cardWidth: CGFloat) -> CGFloat {
        let validWidth = cardWidth.isFinite && cardWidth > 0 ? cardWidth : 360
        return min(max(validWidth * 0.46, 156), 220)
    }

    static func clampedDragOffset(startOffset: CGFloat, translationX: CGFloat, cardWidth: CGFloat) -> CGFloat {
        let maxTravel = max(cardWidth.isFinite && cardWidth > 0 ? cardWidth : 360, actionWidth)
        return min(0, max(-maxTravel, startOffset + translationX))
    }

    static func isPastFullSwipeThreshold(offset: CGFloat, cardWidth: CGFloat) -> Bool {
        offset <= -fullSwipeThreshold(cardWidth: cardWidth)
    }

    static func decideReleaseOutcome(
        startOffset: CGFloat,
        currentOffset: CGFloat,
        velocityX: CGFloat,
        cardWidth: CGFloat,
        actionWidth: CGFloat = actionWidth
    ) -> NativeSwipeReleaseOutcome {
        if isPastFullSwipeThreshold(offset: currentOffset, cardWidth: cardWidth) {
            return .delete
        }
        if startOffset <= -actionWidth * 0.5 {
            if velocityX > 300 || currentOffset > -actionWidth * 0.65 {
                return .closed
            }
            return .open
        }
        if currentOffset <= -revealThreshold(actionWidth: actionWidth)
            || (currentOffset < -16 && velocityX < -350) {
            return .open
        }
        return .closed
    }
}
