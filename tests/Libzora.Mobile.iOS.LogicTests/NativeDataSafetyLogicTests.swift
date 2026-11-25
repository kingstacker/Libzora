import Foundation

@main
struct NativeDataSafetyLogicTests {
    static func main() {
        var state = NativeReflectionEditState()
        assert(!state.canEdit && !state.beginWrite())
        state.beginLoad(); state.finishLoad(succeeded: false)
        assert(!state.canEdit && !state.beginWrite())
        state.beginLoad(); state.finishLoad(succeeded: true)
        assert(state.canEdit && state.beginWrite())
        assert(!state.canEdit && !state.beginWrite())
        state.finishWrite(); assert(state.canEdit)
        assert(!NativeOnboardingPolicy.canOpenReader(confirmedAcceptance: false))
        assert(NativeOnboardingPolicy.canOpenReader(confirmedAcceptance: true))
        assert(NativeOnboardingPolicy.canOpenReader(confirmedAcceptance: false, debugBypass: true))
        var deletion = NativePendingMaterialDeletes()
        assert(deletion.begin("note") && !deletion.begin("note"))
        deletion.finish("note")
        assert(!deletion.includes("note"))
        assert(deletion.begin("note"))
        deletion.finish("note"); deletion.finish("note")
        assert(deletion.ids.isEmpty)
        print("Passed 14 native data-safety regression assertions.")
    }
}
